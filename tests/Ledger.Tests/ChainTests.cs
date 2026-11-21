using Ledger.InMemory;
using Ledger.Monetary;
using static Ledger.Tests.TestSupport;

namespace Ledger.Tests;

/// <summary>Simulates an attacker, or a buggy migration, editing rows behind the ledger's back.</summary>
public class ChainTests
{
    private sealed class TamperStore : DelegatingStore
    {
        public TamperStore(ILedgerStore inner)
            : base(inner)
        {
        }

        public Func<IReadOnlyList<Transaction>, IReadOnlyList<Transaction>>? Transactions { get; set; }

        public Func<string, Balances, Balances>? Balances { get; set; }

        public Func<IReadOnlyList<Entry>, IReadOnlyList<Entry>>? Entries { get; set; }

        public override ValueTask<T> ViewAsync<T>(Func<IStoreReader, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken = default) =>
            Inner.ViewAsync((r, ct) => read(new TamperReader(r, this), ct), cancellationToken);
    }

    private sealed class TamperReader : DelegatingReader
    {
        private readonly TamperStore _store;

        public TamperReader(IStoreReader inner, TamperStore store)
            : base(inner)
        {
            _store = store;
        }

        public override async ValueTask<IReadOnlyList<Transaction>> GetTransactionsAsync(long afterSeq, int limit, CancellationToken cancellationToken)
        {
            IReadOnlyList<Transaction> page = await base.GetTransactionsAsync(afterSeq, limit, cancellationToken);
            return _store.Transactions is { } f ? f(page) : page;
        }

        public override async ValueTask<Balances?> FindBalancesAsync(string id, CancellationToken cancellationToken)
        {
            Balances? b = await base.FindBalancesAsync(id, cancellationToken);
            return b is { } value && _store.Balances is { } f ? f(id, value) : b;
        }

        public override async ValueTask<IReadOnlyList<Entry>> GetEntriesAsync(string accountId, long afterAccountSeq, int limit, CancellationToken cancellationToken)
        {
            IReadOnlyList<Entry> page = await base.GetEntriesAsync(accountId, afterAccountSeq, limit, cancellationToken);
            return _store.Entries is { } f ? f(page) : page;
        }
    }

    private static async Task<(GeneralLedger Ledger, TamperStore Store)> TamperLedgerAsync()
    {
        var store = new TamperStore(new InMemoryLedgerStore());
        var l = new GeneralLedger(store);
        foreach (string id in new[] { "cash", "wallet" })
        {
            await l.OpenAccountAsync(new Account { Id = id, Currency = Currency.BRL, Type = AccountType.Asset });
        }

        for (int i = 0; i < 600; i++)
        {
            // More than one verification page.
            await l.PostAsync(new PostRequest
            {
                Description = "move",
                Metadata = new Dictionary<string, string> { ["i"] = "x" },
                Postings = Legs(Dr("cash", Brl("1.00")), Cr("wallet", Brl("1.00"))),
            });
        }

        return (l, store);
    }

    private static Func<IReadOnlyList<Transaction>, IReadOnlyList<Transaction>> At(long seq, Func<Transaction, Transaction> edit) =>
        page => page.Select(t => t.Seq == seq ? edit(t) : t).ToArray();

    [Fact]
    public async Task VerifyChainDetectsEveryKindOfTampering()
    {
        var (l, store) = await TamperLedgerAsync();
        ChainHead head = await l.VerifyChainAsync();
        Assert.Equal(600, head.Seq);

        var cases = new (string Name, long Seq, Func<IReadOnlyList<Transaction>, IReadOnlyList<Transaction>> Edit)[]
        {
            ("amount", 7, At(7, t => t with { Postings = new[] { t.Postings[0] with { Amount = Brl("1000.00") }, t.Postings[1] } })),
            ("account", 7, At(7, t => t with { Postings = new[] { t.Postings[0], t.Postings[1] with { Account = "attacker" } } })),
            ("direction", 7, At(7, t => t with { Postings = new[] { t.Postings[0] with { Direction = Direction.Credit }, t.Postings[1] } })),
            ("description", 520, At(520, t => t with { Description = "nothing to see" })),
            ("metadata", 3, At(3, t => t with { Metadata = new Dictionary<string, string> { ["i"] = "y" } })),
            ("key", 3, At(3, t => t with { IdempotencyKey = "k" })),
            ("expiry", 3, At(3, t => t with { ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(1) })),
            ("timestamp backwards", 9, At(9, t => t with { Timestamp = t.Timestamp.AddHours(-1) })),
            ("stored hash", 10, At(10, t => t with { Hash = FlipBit(t.Hash, 0) })),
            ("prev hash", 4, At(4, t => t with { PrevHash = FlipBit(t.PrevHash, 5) })),
            ("deletion", 6, page => page.Count > 4 && page[0].Seq == 1 ? page.Where((_, i) => i != 4).ToArray() : page),
            ("reorder", 3, page =>
            {
                if (page.Count <= 2 || page[0].Seq != 1)
                {
                    return page;
                }

                Transaction[] copy = page.ToArray();
                (copy[1], copy[2]) = (copy[2], copy[1]);
                return copy;
            }),
        };
        foreach ((string name, long seq, Func<IReadOnlyList<Transaction>, IReadOnlyList<Transaction>> edit) in cases)
        {
            store.Transactions = edit;
            try
            {
                ChainBrokenException e = await Assert.ThrowsAsync<ChainBrokenException>(async () => await l.VerifyChainAsync());
                Assert.Equal(LedgerError.ChainBroken, e.Code);
                Assert.True(e.Seq == seq, name + ": detected at seq " + e.Seq + " (" + e.Reason + ")");
            }
            finally
            {
                store.Transactions = null;
            }
        }
    }

    [Fact]
    public async Task VerifyBalancesDetectsEditedBalancesAndEntries()
    {
        var (l, store) = await TamperLedgerAsync();
        await l.VerifyBalancesAsync();

        store.Balances = (id, b) => id == "wallet" ? b with { CreditsPosted = b.CreditsPosted + 1 } : b;
        await RejectsAsync(LedgerError.BalanceMismatch, () => l.VerifyBalancesAsync());
        store.Balances = null;

        store.Entries = es => es.Select((e, i) => i == 3 ? e with { Amount = e.Amount + 1 } : e).ToArray();
        await RejectsAsync(LedgerError.BalanceMismatch, () => l.VerifyBalancesAsync());

        store.Entries = es => es.Select(e => e.Phase == EntryPhase.Posted ? e with { Phase = EntryPhase.Released } : e).ToArray();
        await RejectsAsync(LedgerError.BalanceMismatch, () => l.VerifyBalancesAsync());
    }

    [Fact]
    public async Task SealIsStableAndCoversEveryField()
    {
        var (l, _) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "10.00");
        Transaction t = (await l.GetTransactionsAsync(0, 10))[0];
        Assert.Equal(t.Hash, Canonical.Seal(t));
        Assert.True(t.PrevHash.IsZero);
        Assert.Equal(t.Hash, Sha256Hash.Parse(t.Hash.ToString()));
        Assert.Equal(64, t.Hash.ToString().Length);

        Transaction[] variants =
        {
            t with { Id = "other" },
            t with { Seq = 2 },
            t with { Kind = TransactionKind.Pending },
            t with { Ref = "x" },
            t with { Timestamp = t.Timestamp.AddTicks(1) },
            t with { Description = "x" },
            t with { RequestHash = FlipBit(t.RequestHash, 31) },
            t with { PrevHash = FlipBit(t.PrevHash, 0) },
        };
        foreach (Transaction v in variants)
        {
            Assert.NotEqual(t.Hash, Canonical.Seal(v));
        }
    }
}
