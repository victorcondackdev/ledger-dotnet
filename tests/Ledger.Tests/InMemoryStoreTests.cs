using Ledger.InMemory;
using Ledger.Monetary;

namespace Ledger.Tests;

public class InMemoryStoreTests
{
    private static Account NewAccount(string id) =>
        new() { Id = id, Currency = Currency.BRL, Type = AccountType.Asset, Normal = Direction.Debit };

    private static async Task<InMemoryLedgerStore> SeedAsync()
    {
        var s = new InMemoryLedgerStore();
        foreach (string id in new[] { "a", "b" })
        {
            await s.UpdateAsync(new[] { id }, async (tx, ct) =>
            {
                await tx.CreateAccountAsync(NewAccount(id), ct);
                return true;
            });
        }

        return s;
    }

    private static async ValueTask AppendAsync(IStoreTransaction tx, string id, string? key, TransactionKind kind, string? reference)
    {
        ChainHead head = await tx.GetChainHeadAsync(CancellationToken.None);
        DateTimeOffset at = DateTimeOffset.FromUnixTimeSeconds(head.Seq);
        var t = new Transaction
        {
            Id = id,
            Seq = head.Seq + 1,
            Kind = kind,
            IdempotencyKey = key,
            Ref = reference,
            Timestamp = at,
            PrevHash = head.Hash,
            Postings = Array.Empty<Posting>(),
        };
        var entry = new Entry { AccountSeq = 1, Seq = t.Seq, TransactionId = id, Account = "a", Time = at };
        await tx.AppendTransactionAsync(t, new[] { entry }, CancellationToken.None);
    }

    [Fact]
    public async Task LockDisciplineIsEnforced()
    {
        InMemoryLedgerStore s = await SeedAsync();
        await s.UpdateAsync(new[] { "a" }, async (tx, ct) =>
        {
            CancellationToken none = CancellationToken.None;
            var attempts = new (string Name, Func<ValueTask> Action)[]
            {
                ("balance", async () => await tx.FindBalancesAsync("b", none)),
                ("put balance", () => tx.PutBalancesAsync("b", default, none)),
                ("create", () => tx.CreateAccountAsync(NewAccount("c"), none)),
                ("update account", () => tx.UpdateAccountAsync(NewAccount("b"), none)),
            };
            foreach ((string name, Func<ValueTask> action) in attempts)
            {
                LedgerException e = await Assert.ThrowsAsync<LedgerException>(async () => await action());
                Assert.True(e.Code == LedgerError.NotLocked, name);
            }

            var x = new Transaction { Id = "t", Seq = 1, Postings = Array.Empty<Posting>() };
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await tx.AppendTransactionAsync(x, Array.Empty<Entry>(), none));
            await tx.GetChainHeadAsync(none);
            var foreign = new Entry { TransactionId = "t", Account = "b" };
            LedgerException unlocked = await Assert.ThrowsAsync<LedgerException>(async () => await tx.AppendTransactionAsync(x, new[] { foreign }, none));
            Assert.Equal(LedgerError.NotLocked, unlocked.Code);
            return false;
        });
    }

    [Fact]
    public async Task ReadsSeeOwnWritesAndRollbackLeavesNoTrace()
    {
        InMemoryLedgerStore s = await SeedAsync();
        var boom = new InvalidOperationException("boom");
        InvalidOperationException thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () => await s.UpdateAsync(new[] { "a", "b", "a", "c" }, async (tx, ct) =>
        {
            await tx.CreateAccountAsync(NewAccount("c"), ct);
            LedgerException dup = await Assert.ThrowsAsync<LedgerException>(async () => await tx.CreateAccountAsync(NewAccount("a"), ct));
            Assert.Equal(LedgerError.AccountExists, dup.Code);
            IReadOnlyList<Account> accounts = await tx.GetAccountsAsync(ct);
            Assert.Equal(new[] { "a", "b", "c" }, accounts.Select(a => a.Id));

            await tx.UpdateAccountAsync(NewAccount("a") with { Flags = AccountFlags.Frozen }, ct);
            Assert.Equal(AccountFlags.Frozen, (await tx.FindAccountAsync("a", ct))!.Flags);
            await Assert.ThrowsAsync<LedgerException>(async () => await tx.UpdateAccountAsync(NewAccount("zz"), ct));

            await tx.PutBalancesAsync("a", new Balances { DebitsPosted = 7 }, ct);
            Assert.Equal(7, (await tx.FindBalancesAsync("a", ct))!.Value.DebitsPosted);
            Assert.Equal(default(Balances), (await tx.FindBalancesAsync("b", ct))!.Value);

            await AppendAsync(tx, "t1", "k1", TransactionKind.Standard, null);
            await AppendAsync(tx, "t2", null, TransactionKind.Reversal, "t1");
            Assert.Equal(2, (await tx.GetChainHeadAsync(ct)).Seq);
            Assert.Equal("t1", (await tx.FindTransactionAsync("t2", ct))!.Ref);
            Assert.Equal("t1", (await tx.FindTransactionByIdempotencyKeyAsync("k1", ct))!.Id);
            Assert.Equal("t2", await tx.FindReversalAsync("t1", ct));
            await tx.PutPendingAsync(new PendingTransfer { Id = "t1", Status = PendingStatus.Open, Postings = Array.Empty<Posting>() }, ct);
            Assert.Equal(PendingStatus.Open, (await tx.FindPendingAsync("t1", ct))!.Status);
            throw boom;
        }));
        Assert.Same(boom, thrown);

        // Nothing leaked.
        await s.ViewAsync<bool>(async (r, ct) =>
        {
            Assert.Null(await r.FindAccountAsync("c", ct));
            Assert.Equal(AccountFlags.None, (await r.FindAccountAsync("a", ct))!.Flags);
            Assert.Equal(0, (await r.FindBalancesAsync("a", ct))!.Value.DebitsPosted);
            Assert.Empty(await r.GetTransactionsAsync(0, 10, ct));
            Assert.Null(await r.FindPendingAsync("t1", ct));
            Assert.Null(await r.FindBalancesAsync("zz", ct));
            return true;
        });
    }

    [Fact]
    public async Task CommitEnforcesUniqueConstraints()
    {
        InMemoryLedgerStore s = await SeedAsync();
        string[] lockA = { "a" };
        await s.UpdateAsync(lockA, async (tx, _) =>
        {
            await AppendAsync(tx, "t1", "k", TransactionKind.Standard, null);
            return true;
        });

        LedgerException dup = await Assert.ThrowsAsync<LedgerException>(async () => await s.UpdateAsync(lockA, async (tx, _) =>
        {
            await AppendAsync(tx, "t2", "k", TransactionKind.Standard, null);
            return true;
        }));
        Assert.Equal(LedgerError.DuplicateIdempotencyKey, dup.Code);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await s.UpdateAsync(lockA, async (tx, _) =>
        {
            await AppendAsync(tx, "t1", null, TransactionKind.Standard, null);
            return true;
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await s.UpdateAsync(lockA, async (tx, ct) =>
        {
            await tx.GetChainHeadAsync(ct);
            await tx.AppendTransactionAsync(new Transaction { Id = "t9", Seq = 9, Postings = Array.Empty<Posting>() }, Array.Empty<Entry>(), ct);
            return true;
        }));

        // Two creators of the same account: the account lock serializes them.
        Task<bool>[] creators = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            try
            {
                await s.UpdateAsync(new[] { "new" }, async (tx, ct) =>
                {
                    await tx.CreateAccountAsync(NewAccount("new"), ct);
                    return true;
                });
                return true;
            }
            catch (LedgerException e) when (e.Code == LedgerError.AccountExists)
            {
                return false;
            }
        })).ToArray();
        bool[] results = await Task.WhenAll(creators);
        Assert.Single(results.Where(r => r));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await s.UpdateAsync(lockA, (_, _) => ValueTask.FromResult(true), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await s.ViewAsync((_, _) => ValueTask.FromResult(true), cts.Token));
    }

    [Fact]
    public async Task ReaderRangesAndOrdering()
    {
        InMemoryLedgerStore s = await SeedAsync();
        for (int i = 0; i < 5; i++)
        {
            int n = i;
            await s.UpdateAsync(new[] { "a" }, async (tx, ct) =>
            {
                await tx.PutPendingAsync(
                    new PendingTransfer
                    {
                        Id = ((char)('p' + n)).ToString(),
                        Status = PendingStatus.Open,
                        ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(10 - (n % 2)),
                        Postings = Array.Empty<Posting>(),
                    },
                    ct);
                ChainHead head = await tx.GetChainHeadAsync(ct);
                DateTimeOffset at = DateTimeOffset.FromUnixTimeSeconds(n * 10);
                var t = new Transaction { Id = ((char)('a' + n)).ToString(), Seq = head.Seq + 1, Timestamp = at, Postings = Array.Empty<Posting>() };
                await tx.AppendTransactionAsync(t, new[] { new Entry { AccountSeq = n + 1, Seq = t.Seq, TransactionId = t.Id, Account = "a", Time = at } }, ct);
                return true;
            });
        }

        await s.UpdateAsync(new[] { "a" }, async (tx, ct) =>
        {
            Assert.Equal(2, (await tx.GetTransactionsAsync(3, 10, ct)).Count);
            Assert.Equal(2, (await tx.GetTransactionsAsync(0, 2, ct)).Count);
            Assert.Empty(await tx.GetTransactionsAsync(9, 2, ct));
            Assert.Empty(await tx.GetTransactionsAsync(0, 0, ct));
            Assert.Equal(2, (await tx.GetEntriesAsync("a", 1, 2, ct)).Count);
            Assert.Single(await tx.GetEntriesAsync("a", 4, 10, ct));
            Assert.Empty(await tx.GetEntriesAsync("b", 0, 10, ct));
            Assert.Equal(3, (await tx.FindEntryAtAsync("a", DateTimeOffset.FromUnixTimeSeconds(25), ct))!.AccountSeq);
            Assert.Null(await tx.FindEntryAtAsync("a", DateTimeOffset.FromUnixTimeSeconds(-1), ct));

            IReadOnlyList<PendingTransfer> due = await tx.GetExpiredPendingAsync(DateTimeOffset.FromUnixTimeSeconds(9), 2, ct);
            Assert.Equal(new[] { "q", "s" }, due.Select(p => p.Id));
            Assert.Equal(5, (await tx.GetExpiredPendingAsync(DateTimeOffset.FromUnixTimeSeconds(100), 10, ct)).Count);
            Assert.Null(await tx.FindPendingAsync("zz", ct));
            Assert.Null(await tx.FindTransactionAsync("zz", ct));
            Assert.Null(await tx.FindTransactionByIdempotencyKeyAsync("zz", ct));
            Assert.Null(await tx.FindReversalAsync("a", ct));
            Assert.Null(await tx.FindAccountAsync("zz", ct));
            Assert.Equal(2, (await tx.GetAccountsAsync(ct)).Count);
            return false;
        });
    }

    // Opposite-direction updates over the same pair of accounts would deadlock
    // without ordered locking.
    [Fact]
    public async Task OpposingLockOrdersDoNotDeadlock()
    {
        InMemoryLedgerStore s = await SeedAsync();
        Task[] workers = Enumerable.Range(0, 8).Select(g => Task.Run(async () =>
        {
            string[] order = g % 2 == 0 ? new[] { "a", "b" } : new[] { "b", "a" };
            for (int i = 0; i < 200; i++)
            {
                await s.UpdateAsync(order, async (tx, ct) =>
                {
                    Balances b = (await tx.FindBalancesAsync(order[0], ct))!.Value;
                    await tx.PutBalancesAsync(order[0], b with { DebitsPosted = b.DebitsPosted + 1 }, ct);
                    return true;
                });
            }
        })).ToArray();
        Task all = Task.WhenAll(workers);
        Task winner = await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(all, winner);
        await all;

        long total = await s.ViewAsync(async (r, ct) =>
            (await r.FindBalancesAsync("a", ct))!.Value.DebitsPosted + (await r.FindBalancesAsync("b", ct))!.Value.DebitsPosted);
        Assert.Equal(8 * 200, total);
    }

    [Fact]
    public async Task ViewsNeverSeeHalfACommit()
    {
        var (l, _) = await TestSupport.BankAsync();
        await TestSupport.DepositAsync(l, "wallet:alice", "1000.00");
        using var stop = new CancellationTokenSource();
        Task auditor = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TrialBalance tb = await l.GetTrialBalanceAsync();
                tb.Check();
            }
        });
        for (int i = 0; i < 500; i++)
        {
            await l.PostAsync(new PostRequest
            {
                Postings = TestSupport.Legs(TestSupport.Dr("wallet:alice", TestSupport.Brl("1.00")), TestSupport.Cr("wallet:bob", TestSupport.Brl("1.00"))),
            });
        }

        stop.Cancel();
        await auditor;
    }
}
