using System.Collections.Concurrent;
using System.Globalization;
using Ledger.InMemory;
using Ledger.Monetary;
using static Ledger.Tests.TestSupport;

namespace Ledger.Tests;

/// <summary>
/// Drives random concurrent traffic against one ledger and checks global
/// invariants, both continuously (from a concurrent auditor) and at the end.
/// Each worker draws from its own seeded generator, so every worker's
/// operation sequence is reproducible; with one worker the whole run is,
/// down to the final chain hash.
/// </summary>
public class SimulationTests
{
    private const int Wallets = 12;
    private const long Funding = 1000_00; // per wallet, in centavos
    private const int Seed = 20261121;

    private sealed class SimClock : TimeProvider
    {
        private long _ticks = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero).UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    private sealed record SimResult(ChainHead Head, long Committed, int Audits, IReadOnlyDictionary<string, int> Stats);

    private static string Wallet(int i) => "wallet:" + i.ToString("00", CultureInfo.InvariantCulture);

    private static async Task<LedgerError?> AttemptAsync(Func<Task> operation, params LedgerError[] allowed)
    {
        try
        {
            await operation();
            return null;
        }
        catch (LedgerException e) when (Array.IndexOf(allowed, e.Code) >= 0)
        {
            return e.Code;
        }
    }

    private static async Task<SimResult> RunAsync(int seed, int workers, int ops)
    {
        var clock = new SimClock();
        var ids = new SequentialIds();
        long committed = 0;
        var l = new GeneralLedger(new InMemoryLedgerStore(), new LedgerOptions
        {
            TimeProvider = clock,
            IdGenerator = ids.Next,
            OnCommit = new Action<Transaction>[] { _ => Interlocked.Increment(ref committed) },
        });

        var chart = new List<Account>
        {
            new() { Id = "asset:settlement", Currency = Currency.BRL, Type = AccountType.Asset },
            new() { Id = "revenue:fees", Currency = Currency.BRL, Type = AccountType.Revenue },
            new() { Id = "payable:partner", Currency = Currency.BRL, Type = AccountType.Liability },
        };
        for (int i = 0; i < Wallets; i++)
        {
            chart.Add(new Account { Id = Wallet(i), Currency = Currency.BRL, Type = AccountType.Liability, Flags = AccountFlags.NoOverdraft });
        }

        foreach (Account a in chart)
        {
            await l.OpenAccountAsync(a);
        }

        var funding = new Money(Funding, Currency.BRL);
        for (int i = 0; i < Wallets; i++)
        {
            await l.PostAsync(new PostRequest
            {
                IdempotencyKey = "fund-" + i.ToString(CultureInfo.InvariantCulture),
                Postings = Legs(Dr("asset:settlement", funding), Cr(Wallet(i), funding)),
            });
        }

        // The auditor checks the trial balance from consistent snapshots while
        // transfers are in flight.
        using var stop = new CancellationTokenSource();
        Task<int> auditor = Task.Run(async () =>
        {
            int audits = 0;
            while (!stop.IsCancellationRequested)
            {
                TrialBalance tb = await l.GetTrialBalanceAsync();
                tb.Check();
                audits++;
                await Task.Delay(1);
            }

            return audits;
        });

        var keys = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var stats = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        void Record(string op, LedgerError? error) => stats.AddOrUpdate(error is null ? op : op + " (rejected)", 1, (_, n) => n + 1);

        async Task WorkerAsync(int w)
        {
            var rng = new Random(unchecked((seed * 31) + w));
            var posted = new List<(PostRequest Request, Transaction Tx)>();
            var holds = new List<string>();
            int n = 0;
            string Key() => "w" + w.ToString(CultureInfo.InvariantCulture) + "-" + (++n).ToString(CultureInfo.InvariantCulture);
            (string From, string To) Pair()
            {
                int a = rng.Next(Wallets);
                int b = (a + 1 + rng.Next(Wallets - 1)) % Wallets;
                return (Wallet(a), Wallet(b));
            }

            for (int i = 0; i < ops; i++)
            {
                int p = rng.Next(100);
                if (p < 40)
                {
                    // Pix with a 1% fee split 70/30.
                    (string from, string to) = Pair();
                    var amount = new Money(1 + rng.NextInt64(200_00), Currency.BRL);
                    var fee = new Money((amount.MinorUnits / 100) + 1, Currency.BRL);
                    Money[] split = fee.Allocate(70, 30);
                    var legs = new List<Posting> { Dr(from, amount + fee), Cr(to, amount), Cr("revenue:fees", split[0]) };
                    if (split[1].IsPositive)
                    {
                        legs.Add(Cr("payable:partner", split[1]));
                    }

                    string key = Key();
                    var request = new PostRequest { IdempotencyKey = key, Description = "pix", Postings = legs };
                    Transaction? tx = null;
                    LedgerError? error = await AttemptAsync(async () => tx = await l.PostAsync(request), LedgerError.InsufficientFunds, LedgerError.AccountFrozen);
                    Record("post", error);
                    if (error is null)
                    {
                        posted.Add((request, tx!));
                        keys[key] = tx!.Id;
                    }
                }
                else if (p < 55)
                {
                    (string from, string to) = Pair();
                    var amount = new Money(1 + rng.NextInt64(100_00), Currency.BRL);
                    TimeSpan timeout = TimeSpan.FromMinutes(rng.Next(3));
                    Transaction? tx = null;
                    LedgerError? error = await AttemptAsync(
                        async () => tx = await l.HoldAsync(new PostRequest { IdempotencyKey = Key(), Timeout = timeout, Postings = Legs(Dr(from, amount), Cr(to, amount)) }),
                        LedgerError.InsufficientFunds,
                        LedgerError.AccountFrozen);
                    Record("hold", error);
                    if (error is null)
                    {
                        holds.Add(tx!.Id);
                    }
                }
                else if (p < 70 && holds.Count > 0)
                {
                    int k = rng.Next(holds.Count);
                    string id = holds[k];
                    holds.RemoveAt(k);
                    LedgerError[] allowed = { LedgerError.NotPending, LedgerError.PendingExpired, LedgerError.AccountFrozen };
                    LedgerError? error;
                    switch (rng.Next(3))
                    {
                        case 0:
                            error = await AttemptAsync(async () => await l.VoidPendingAsync(new ResolveRequest { Id = id, IdempotencyKey = Key() }), allowed);
                            Record("void", error);
                            break;
                        case 1:
                            error = await AttemptAsync(async () => await l.PostPendingAsync(new ResolveRequest { Id = id, IdempotencyKey = Key() }), allowed);
                            Record("post pending", error);
                            break;
                        default:
                            PendingTransfer pt = await l.GetPendingAsync(id);
                            var amount = new Money(1 + rng.NextInt64(pt.Postings[0].Amount.MinorUnits), Currency.BRL);
                            error = await AttemptAsync(async () => await l.PostPendingAsync(new ResolveRequest { Id = id, IdempotencyKey = Key(), Amount = amount }), allowed);
                            Record("partial post", error);
                            break;
                    }

                    if (error is LedgerError.PendingExpired or LedgerError.AccountFrozen)
                    {
                        holds.Add(id); // still open: release it at the end
                    }
                }
                else if (p < 80 && posted.Count > 0)
                {
                    Transaction original = posted[rng.Next(posted.Count)].Tx;
                    LedgerError? error = await AttemptAsync(
                        async () => await l.ReverseAsync(new ResolveRequest { Id = original.Id, IdempotencyKey = Key() }),
                        LedgerError.AlreadyReversed,
                        LedgerError.InsufficientFunds,
                        LedgerError.AccountFrozen);
                    Record("reverse", error);
                }
                else if (p < 90 && posted.Count > 0)
                {
                    // Retry: must replay.
                    (PostRequest request, Transaction tx) = posted[rng.Next(posted.Count)];
                    Transaction again = await l.PostAsync(request);
                    Assert.Equal(tx.Id, again.Id);
                    Assert.Equal(tx.Hash, again.Hash);
                    Record("replay", null);
                }
                else if (p < 95 && posted.Count > 0)
                {
                    // Reused key, different request.
                    PostRequest request = posted[rng.Next(posted.Count)].Request;
                    await RejectsAsync(LedgerError.IdempotencyConflict, () => l.PostAsync(request with { Description = "tampered" }));
                    Record("conflict", null);
                }
                else if (p < 98)
                {
                    // Time passes; sweep expired holds.
                    clock.Advance(TimeSpan.FromSeconds(1 + rng.Next(30)));
                    await l.ExpirePendingAsync();
                    Record("expire sweep", null);
                }
                else
                {
                    // A brief judicial block.
                    string id = Wallet(rng.Next(Wallets));
                    await l.FreezeAsync(id);
                    await l.UnfreezeAsync(id);
                    Record("freeze", null);
                }
            }

            // Release everything this worker still holds.
            foreach (string id in holds)
            {
                await AttemptAsync(async () => await l.VoidPendingAsync(new ResolveRequest { Id = id }), LedgerError.NotPending);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, workers).Select(w => Task.Run(() => WorkerAsync(w))));
        stop.Cancel();
        int auditCount = await auditor;

        // Global invariants.
        TrialBalance final = await l.GetTrialBalanceAsync();
        final.Check();
        long liabilities = 0;
        foreach (TrialBalanceRow row in final.Rows)
        {
            Assert.True(row.DebitsPending.IsZero && row.CreditsPending.IsZero, row.Account.Id + ": pending left after all holds were resolved");
            Assert.False((row.Account.Flags & AccountFlags.NoOverdraft) != 0 && row.Balance.IsNegative, row.Account.Id + ": no-overdraft account is negative");
            if (row.Account.Normal == Direction.Credit)
            {
                liabilities += row.Balance.MinorUnits;
            }
        }

        // Transfers, fees, holds and reversals move money between accounts but
        // never create or destroy it.
        Assert.Equal(Wallets * Funding, liabilities);
        Assert.Equal(Wallets * Funding, (await l.GetBalanceAsync("asset:settlement")).Posted.MinorUnits);
        ChainHead head = await l.VerifyChainAsync();
        await l.VerifyBalancesAsync();
        Assert.Equal(head.Seq, Interlocked.Read(ref committed));

        // Every key maps to exactly the transaction its caller saw.
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        for (long after = 0; ;)
        {
            IReadOnlyList<Transaction> page = await l.GetTransactionsAsync(after, 1000);
            foreach (Transaction x in page)
            {
                if (x.IdempotencyKey is { } key)
                {
                    Assert.True(seen.TryAdd(key, x.Id), "key " + key + " used twice");
                }
            }

            if (page.Count < 1000)
            {
                break;
            }

            after = page[^1].Seq;
        }

        foreach (KeyValuePair<string, string> kv in keys)
        {
            Assert.Equal(kv.Value, seen[kv.Key]);
        }

        return new SimResult(head, Interlocked.Read(ref committed), auditCount, stats);
    }

    [Fact]
    public async Task ConcurrentTrafficPreservesInvariants()
    {
        SimResult r = await RunAsync(Seed, workers: 8, ops: 1500);
        Assert.True(r.Head.Seq > 1000, "too few transactions: " + r.Head.Seq);
        Assert.True(r.Stats.ContainsKey("post") && r.Stats.ContainsKey("replay") && r.Stats.ContainsKey("conflict"));
    }

    // With a single worker the run is fully deterministic: the same seed
    // yields the same history, byte for byte, as witnessed by the chain head.
    [Fact]
    public async Task SingleWorkerRunsAreReproducible()
    {
        SimResult a = await RunAsync(Seed, workers: 1, ops: 1500);
        SimResult b = await RunAsync(Seed, workers: 1, ops: 1500);
        Assert.Equal(a.Head, b.Head);
        SimResult c = await RunAsync(Seed + 1, workers: 1, ops: 1500);
        Assert.NotEqual(a.Head.Hash, c.Head.Hash);
    }
}
