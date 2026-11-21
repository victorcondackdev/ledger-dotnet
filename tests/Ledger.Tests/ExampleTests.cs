using Ledger.InMemory;
using Ledger.Monetary;
using static Ledger.Tests.TestSupport;

namespace Ledger.Tests;

/// <summary>The runnable examples of the Go implementation, with the same expected output.</summary>
public class ExampleTests
{
    private static async Task<string> BalanceLineAsync(GeneralLedger l, string id)
    {
        Balance b = await l.GetBalanceAsync(id);
        return id.PadRight(16) + " posted " + b.Posted.Format(MoneyLocale.PtBR) + ", available " + b.Available.Format(MoneyLocale.PtBR);
    }

    // A Pix transfer from Alice to Bob with a R$ 0,99 fee that the platform
    // shares 70/30 with a partner. Allocate splits the fee without losing a
    // centavo, and the whole transfer is one balanced transaction.
    [Fact]
    public async Task PixTransferWithFee()
    {
        var (l, _) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "1000.00");
        var output = new List<string>();

        Money amount = Brl("250.00");
        Money fee = Brl("0.99");
        Money[] split = fee.Allocate(70, 30);
        var request = new PostRequest
        {
            IdempotencyKey = "E00000000202611020900abcdef123456",
            Description = "Pix to Bob",
            Metadata = new Dictionary<string, string> { ["end_to_end_id"] = "E00000000202611020900abcdef123456" },
            Postings = Legs(Dr("wallet:alice", amount + fee), Cr("wallet:bob", amount), Cr("revenue:fees", split[0]), Cr("payable:partner", split[1])),
        };
        Transaction tx = await l.PostAsync(request);
        output.Add(tx.Id + " " + tx.Kind.ToCode() + " seq " + tx.Seq);
        foreach (string id in new[] { "wallet:alice", "wallet:bob", "revenue:fees", "payable:partner" })
        {
            output.Add(await BalanceLineAsync(l, id));
        }

        // A retry with the same EndToEndId is answered with the original.
        Transaction again = await l.PostAsync(request with { Postings = tx.Postings });
        output.Add("replay returns " + again.Id);

        // Overdrafts are rejected atomically: nothing is written.
        LedgerException e = await Assert.ThrowsAsync<LedgerException>(async () =>
            await l.PostAsync(new PostRequest { Postings = Legs(Dr("wallet:bob", Brl("999.00")), Cr("wallet:alice", Brl("999.00"))) }));
        output.Add((e.Code == LedgerError.InsufficientFunds).ToString());

        Assert.Equal(
            new[]
            {
                "tx-002 standard seq 2",
                "wallet:alice     posted R$ 749,01, available R$ 749,01",
                "wallet:bob       posted R$ 250,00, available R$ 250,00",
                "revenue:fees     posted R$ 0,69, available R$ 0,69",
                "payable:partner  posted R$ 0,30, available R$ 0,30",
                "replay returns tx-002",
                "True",
            },
            output);
    }

    // A card authorization holds R$ 120,00; the merchant captures only
    // R$ 100,00 and the remaining R$ 20,00 is released.
    [Fact]
    public async Task TwoPhaseTransfer()
    {
        var (l, _) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "150.00");
        var output = new List<string>();

        Transaction hold = await l.HoldAsync(new PostRequest
        {
            IdempotencyKey = "auth-7781",
            Description = "Card authorization, Padaria",
            Postings = Legs(Dr("wallet:alice", Brl("120.00")), Cr("wallet:bob", Brl("120.00"))),
            Timeout = TimeSpan.FromDays(7),
        });
        output.Add("hold expires " + hold.ExpiresAt!.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        output.Add(await BalanceLineAsync(l, "wallet:alice"));

        Transaction post = await l.PostPendingAsync(new ResolveRequest
        {
            Id = hold.Id,
            IdempotencyKey = "capture-7781",
            Description = "Card capture",
            Amount = Brl("100.00"),
        });
        PendingTransfer pt = await l.GetPendingAsync(hold.Id);
        output.Add(pt.Status.ToCode() + " by " + pt.ResolvedBy + " ref " + post.Ref);
        output.Add(await BalanceLineAsync(l, "wallet:alice"));
        output.Add(await BalanceLineAsync(l, "wallet:bob"));
        LedgerException e = await Assert.ThrowsAsync<LedgerException>(async () => await l.VoidPendingAsync(new ResolveRequest { Id = hold.Id }));
        output.Add((e.Code == LedgerError.NotPending).ToString());

        Assert.Equal(
            new[]
            {
                "hold expires 2026-11-09",
                "wallet:alice     posted R$ 150,00, available R$ 30,00",
                "posted by tx-003 ref tx-002",
                "wallet:alice     posted R$ 50,00, available R$ 50,00",
                "wallet:bob       posted R$ 100,00, available R$ 100,00",
                "True",
            },
            output);
    }

    // Holds with a timeout are voided by the expiry sweep once due, e.g. funds
    // blocked under a Pix MED claim that the bank did not act on in time.
    [Fact]
    public async Task Expiry()
    {
        var (l, clock) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "80.00");
        Transaction hold = await l.HoldAsync(new PostRequest
        {
            Description = "MED block",
            Postings = Legs(Dr("wallet:alice", Brl("80.00")), Cr("payable:partner", Brl("80.00"))),
            Timeout = TimeSpan.FromHours(72),
        });
        Assert.Equal(0, await l.ExpirePendingAsync());

        clock.Advance(TimeSpan.FromHours(72));
        await RejectsAsync(LedgerError.PendingExpired, () => l.PostPendingAsync(new ResolveRequest { Id = hold.Id }));
        Assert.Equal(1, await l.ExpirePendingAsync());
        Assert.Equal(PendingStatus.Expired, (await l.GetPendingAsync(hold.Id)).Status);
        Assert.Equal("wallet:alice     posted R$ 80,00, available R$ 80,00", await BalanceLineAsync(l, "wallet:alice"));
    }

    // A refund is a reversal: a new transaction mirroring the original and
    // linked to it. The original stays untouched in the audit chain.
    [Fact]
    public async Task Reversal()
    {
        var (l, _) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "40.00");
        Transaction pay = await l.PostAsync(new PostRequest
        {
            Description = "Pix to Bob",
            Postings = Legs(Dr("wallet:alice", Brl("25.00")), Cr("wallet:bob", Brl("25.00"))),
        });
        Transaction rev = await l.ReverseAsync(new ResolveRequest { Id = pay.Id, IdempotencyKey = "refund-1", Description = "Pix refund (devolu\u00E7\u00E3o)" });
        var output = new List<string> { rev.Id + " " + rev.Kind.ToCode() + " of " + rev.Ref };
        output.AddRange(rev.Postings.Select(p => "  " + p.Direction.ToCode() + " " + p.Account + " " + p.Amount.Format(MoneyLocale.PtBR)));
        output.Add(await BalanceLineAsync(l, "wallet:alice"));
        await RejectsAsync(LedgerError.AlreadyReversed, () => l.ReverseAsync(new ResolveRequest { Id = pay.Id }));
        ChainHead head = await l.VerifyChainAsync();
        output.Add("chain ok: head seq " + head.Seq);

        Assert.Equal(
            new[]
            {
                "tx-003 reversal of tx-002",
                "  credit wallet:alice R$ 25,00",
                "  debit wallet:bob R$ 25,00",
                "wallet:alice     posted R$ 40,00, available R$ 40,00",
                "chain ok: head seq 3",
            },
            output);
    }

    // A statement with running balances, paginated with an opaque cursor, and
    // the trial balance that proves the books are balanced.
    [Fact]
    public async Task StatementAndTrialBalance()
    {
        var (l, clock) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "500.00");
        string[] amounts = { "12.50", "7.25", "30.00" };
        for (int i = 0; i < amounts.Length; i++)
        {
            clock.Advance(TimeSpan.FromHours(1));
            await l.PostAsync(new PostRequest
            {
                Description = "Pix to Bob #" + (i + 1),
                Postings = Legs(Dr("wallet:alice", Brl(amounts[i])), Cr("wallet:bob", Brl(amounts[i]))),
            });
        }

        var output = new List<string>();
        var options = new StatementOptions { Limit = 2 };
        for (int page = 1; ; page++)
        {
            Statement st = await l.GetStatementAsync("wallet:alice", options);
            output.Add("page " + page);
            foreach (StatementLine ln in st.Lines)
            {
                output.Add("  " + ln.Time.UtcDateTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) + "  "
                    + ln.Description.PadRight(15) + " " + ln.Amount.Format(MoneyLocale.PtBR).PadLeft(12) + " "
                    + ln.Balance.Format(MoneyLocale.PtBR).PadLeft(12));
            }

            if (st.NextCursor is null)
            {
                break;
            }

            options = options with { Cursor = st.NextCursor };
        }

        Balance at = await l.GetBalanceAtAsync("wallet:alice", new DateTimeOffset(2026, 11, 2, 10, 30, 0, TimeSpan.Zero));
        output.Add("balance at 10:30: " + at.Posted.Format(MoneyLocale.PtBR));
        TrialBalance tb = await l.GetTrialBalanceAsync();
        CurrencyTotals t = tb.Totals[Currency.BRL];
        output.Add("trial balance: " + new Money(t.DebitsPosted, Currency.BRL).Format(MoneyLocale.PtBR) + " = "
            + new Money(t.CreditsPosted, Currency.BRL).Format(MoneyLocale.PtBR) + " " + tb.IsBalanced);

        Assert.Equal(
            new[]
            {
                "page 1",
                "  09:00  Pix in             R$ 500,00    R$ 500,00",
                "  10:00  Pix to Bob #1      -R$ 12,50    R$ 487,50",
                "page 2",
                "  11:00  Pix to Bob #2       -R$ 7,25    R$ 480,25",
                "  12:00  Pix to Bob #3      -R$ 30,00    R$ 450,25",
                "balance at 10:30: R$ 487,50",
                "trial balance: R$ 549,75 = R$ 549,75 True",
            },
            output);
    }

    // Hooks receive every committed transaction, e.g. to wake an outbox relay.
    [Fact]
    public async Task CommitHooks()
    {
        var output = new List<string>();
        var ids = new SequentialIds();
        var l = new GeneralLedger(new InMemoryLedgerStore(), new LedgerOptions
        {
            IdGenerator = ids.Next,
            OnCommit = new Action<Transaction>[] { t => output.Add("committed " + t.Seq + " " + t.Id + " " + t.Description) },
        });
        foreach (string id in new[] { "a", "b" })
        {
            await l.OpenAccountAsync(new Account { Id = id, Currency = Currency.USD, Type = AccountType.Asset });
        }

        await l.PostAsync(new PostRequest
        {
            Description = "move",
            Postings = Legs(Dr("a", new Money(100, Currency.USD)), Cr("b", new Money(100, Currency.USD))),
        });
        Assert.Equal(new[] { "committed 1 tx-001 move" }, output);
    }
}
