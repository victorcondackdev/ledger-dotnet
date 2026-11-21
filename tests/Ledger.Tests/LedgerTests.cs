using Ledger.InMemory;
using Ledger.Monetary;
using static Ledger.Tests.TestSupport;

namespace Ledger.Tests;

public class LedgerTests
{
    [Fact]
    public async Task OpenAccountValidatesAndDerivesNormalBalance()
    {
        var (l, _) = await BankAsync();
        var invalid = new (Account Account, LedgerError Error)[]
        {
            (new Account { Id = "", Currency = Currency.BRL, Type = AccountType.Asset }, LedgerError.InvalidAccount),
            (new Account { Id = "x", Currency = new Currency("XXX"), Type = AccountType.Asset }, LedgerError.InvalidAccount),
            (new Account { Id = "x", Currency = Currency.BRL }, LedgerError.InvalidAccount),
            (new Account { Id = "x", Currency = Currency.BRL, Type = AccountType.Asset, Normal = (Direction)9 }, LedgerError.InvalidAccount),
            (new Account { Id = "wallet:alice", Currency = Currency.BRL, Type = AccountType.Liability }, LedgerError.AccountExists),
        };
        foreach ((Account account, LedgerError error) in invalid)
        {
            await RejectsAsync(error, () => l.OpenAccountAsync(account));
        }

        foreach ((AccountType type, Direction normal) in new[]
        {
            (AccountType.Asset, Direction.Debit), (AccountType.Expense, Direction.Debit), (AccountType.Liability, Direction.Credit),
            (AccountType.Equity, Direction.Credit), (AccountType.Revenue, Direction.Credit),
        })
        {
            Account a = await l.OpenAccountAsync(new Account { Id = "n:" + type.ToCode(), Currency = Currency.BRL, Type = type });
            Assert.Equal(normal, a.Normal);
            Assert.Equal(Start, a.CreatedAt);
        }

        Account contra = await l.OpenAccountAsync(new Account { Id = "asset:allowance", Currency = Currency.BRL, Type = AccountType.Asset, Normal = Direction.Credit });
        Assert.Equal(Direction.Credit, contra.Normal);
        Assert.Equal(Direction.Credit, (await l.GetAccountAsync("asset:allowance")).Normal);
        await RejectsAsync(LedgerError.AccountNotFound, () => l.GetAccountAsync("nope"));
        IReadOnlyList<Account> accounts = await l.GetAccountsAsync();
        Assert.Equal(11, accounts.Count);
        Assert.Equal("asset:allowance", accounts[0].Id);
    }

    [Fact]
    public async Task PostValidationLeavesNoTrace()
    {
        var (l, _) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "100.00");
        await l.OpenAccountAsync(new Account { Id = "usd:cash", Currency = Currency.USD, Type = AccountType.Asset });
        await l.OpenAccountAsync(new Account { Id = "usd:wallet", Currency = Currency.USD, Type = AccountType.Liability });
        var many = new List<Posting>();
        for (int i = 0; i < (GeneralLedger.MaxPostings / 2) + 1; i++)
        {
            many.Add(Dr("asset:settlement", Brl("1.00")));
            many.Add(Cr("wallet:bob", Brl("1.00")));
        }

        var max = new Money(long.MaxValue, Currency.BRL);
        var cases = new (string Name, PostRequest Request, LedgerError Error)[]
        {
            ("one posting", new PostRequest { Postings = Legs(Dr("asset:settlement", Brl("1"))) }, LedgerError.InvalidPostings),
            ("no account", new PostRequest { Postings = Legs(Dr("", Brl("1")), Cr("wallet:bob", Brl("1"))) }, LedgerError.InvalidPostings),
            ("no direction", new PostRequest { Postings = Legs(new Posting("wallet:bob", default, Brl("1")), Cr("wallet:bob", Brl("1"))) }, LedgerError.InvalidPostings),
            ("unknown currency", new PostRequest { Postings = Legs(Dr("a", new Money(1, new Currency("XXX"))), Cr("b", new Money(1, new Currency("XXX")))) }, LedgerError.InvalidPostings),
            ("zero", new PostRequest { Postings = Legs(Dr("asset:settlement", Brl("0")), Cr("wallet:bob", Brl("0"))) }, LedgerError.InvalidPostings),
            ("negative", new PostRequest { Postings = Legs(Dr("asset:settlement", Brl("-1")), Cr("wallet:bob", Brl("-1"))) }, LedgerError.InvalidPostings),
            ("unbalanced", new PostRequest { Postings = Legs(Dr("asset:settlement", Brl("1")), Cr("wallet:bob", Brl("2"))) }, LedgerError.Unbalanced),
            ("unbalanced per currency", new PostRequest { Postings = Legs(Dr("asset:settlement", Brl("1")), Cr("usd:wallet", new Money(100, Currency.USD))) }, LedgerError.Unbalanced),
            ("too many", new PostRequest { Postings = many }, LedgerError.InvalidPostings),
            ("timeout on post", new PostRequest { Timeout = TimeSpan.FromSeconds(1), Postings = Legs(Dr("asset:settlement", Brl("1")), Cr("wallet:bob", Brl("1"))) }, LedgerError.InvalidPostings),
            ("unknown account", new PostRequest { Postings = Legs(Dr("asset:settlement", Brl("1")), Cr("wallet:carol", Brl("1"))) }, LedgerError.AccountNotFound),
            ("currency mismatch", new PostRequest { Postings = Legs(Dr("usd:cash", Brl("1")), Cr("wallet:bob", Brl("1"))) }, LedgerError.CurrencyMismatch),
            ("overdraft", new PostRequest { Postings = Legs(Dr("wallet:alice", Brl("100.01")), Cr("wallet:bob", Brl("100.01"))) }, LedgerError.InsufficientFunds),
            ("overflow", new PostRequest { Postings = Legs(Dr("asset:settlement", max), Cr("wallet:bob", max)) }, LedgerError.Overflow),
        };
        foreach ((string name, PostRequest request, LedgerError error) in cases)
        {
            LedgerException e = await Assert.ThrowsAnyAsync<LedgerException>(async () => await l.PostAsync(request));
            Assert.True(e.Code == error, name + ": " + e.Message);
        }

        // Multi-currency transactions balance per currency.
        await l.PostAsync(new PostRequest
        {
            Postings = Legs(
                Dr("asset:settlement", Brl("5")),
                Cr("wallet:bob", Brl("5")),
                Dr("usd:cash", new Money(100, Currency.USD)),
                Cr("usd:wallet", new Money(100, Currency.USD))),
        });
        await WantAvailableAsync(l, "wallet:alice", "100.00", "100.00");
        await WantAvailableAsync(l, "wallet:bob", "5.00", "5.00");
        await AuditAsync(l);
    }

    [Fact]
    public async Task SameAccountOnBothSidesProducesTwoEntries()
    {
        var (l, _) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "10.00");
        Transaction tx = await l.PostAsync(new PostRequest { Postings = Legs(Dr("wallet:alice", Brl("3")), Cr("wallet:alice", Brl("3"))) });
        var seqs = new List<long>();
        var options = new StatementOptions { Limit = 1 };
        while (true)
        {
            Statement st = await l.GetStatementAsync("wallet:alice", options);
            seqs.AddRange(st.Lines.Select(x => x.Seq));
            if (st.NextCursor is null)
            {
                break;
            }

            options = options with { Cursor = st.NextCursor };
        }

        Assert.Equal(new long[] { 1, tx.Seq, tx.Seq }, seqs);
        await AuditAsync(l);
    }

    [Fact]
    public async Task IdempotentReplaysAndConflicts()
    {
        var (l, _) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "100.00");
        var request = new PostRequest
        {
            IdempotencyKey = "k1",
            Description = "Pix",
            Metadata = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" },
            Postings = Legs(Dr("wallet:alice", Brl("10")), Cr("wallet:bob", Brl("10"))),
        };
        Transaction first = await l.PostAsync(request);
        Transaction again = await l.PostAsync(request);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.Hash, again.Hash);
        await WantAvailableAsync(l, "wallet:alice", "90.00", "90.00");

        PostRequest[] conflicts =
        {
            request with { Description = "other" },
            request with { Metadata = new Dictionary<string, string> { ["a"] = "1" } },
            request with { Postings = Legs(Dr("wallet:alice", Brl("11")), Cr("wallet:bob", Brl("11"))) },
        };
        foreach (PostRequest c in conflicts)
        {
            await RejectsAsync(LedgerError.IdempotencyConflict, () => l.PostAsync(c));
        }

        // Keys share one namespace across operations.
        await RejectsAsync(LedgerError.IdempotencyConflict, () => l.HoldAsync(request));
        await RejectsAsync(LedgerError.IdempotencyConflict, () => l.ReverseAsync(new ResolveRequest { Id = first.Id, IdempotencyKey = "k1" }));

        // Reversal replays return the reversal, not AlreadyReversed.
        var reverse = new ResolveRequest { Id = first.Id, IdempotencyKey = "rev-1" };
        Transaction r1 = await l.ReverseAsync(reverse);
        Transaction r2 = await l.ReverseAsync(reverse);
        Assert.Equal(r1.Id, r2.Id);
        await AuditAsync(l);
    }

    [Fact]
    public async Task ConcurrentRequestsWithOneKeyCommitOnce()
    {
        var (l, _) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "100.00");
        var request = new PostRequest { IdempotencyKey = "same", Postings = Legs(Dr("wallet:alice", Brl("1")), Cr("wallet:bob", Brl("1"))) };
        Task<Transaction>[] tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(async () => await l.PostAsync(request))).ToArray();
        Transaction[] results = await Task.WhenAll(tasks);
        Assert.Single(results.Select(t => t.Id).Distinct());
        await WantAvailableAsync(l, "wallet:alice", "99.00", "99.00");
    }

    /// <summary>Hides idempotency keys from the first lookups, reproducing the race in which two writers both see a key as free and the store's unique constraint decides at commit.</summary>
    private sealed class HidingStore : DelegatingStore
    {
        private int _hides;

        public HidingStore(ILedgerStore inner)
            : base(inner)
        {
        }

        public int Hides
        {
            get => Volatile.Read(ref _hides);
            set => Volatile.Write(ref _hides, value);
        }

        public override ValueTask UpdateAsync(IReadOnlyList<string> lockAccounts, Func<IStoreTransaction, CancellationToken, ValueTask<bool>> work, CancellationToken cancellationToken = default) =>
            Inner.UpdateAsync(lockAccounts, (tx, ct) => work(new HidingTransaction(tx, this), ct), cancellationToken);

        public bool TakeHide()
        {
            while (true)
            {
                int current = Volatile.Read(ref _hides);
                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _hides, current - 1, current) == current)
                {
                    return true;
                }
            }
        }
    }

    private sealed class HidingTransaction : DelegatingTransaction
    {
        private readonly HidingStore _store;

        public HidingTransaction(IStoreTransaction inner, HidingStore store)
            : base(inner)
        {
            _store = store;
        }

        public override ValueTask<Transaction?> FindTransactionByIdempotencyKeyAsync(string key, CancellationToken cancellationToken) =>
            _store.TakeHide() ? ValueTask.FromResult<Transaction?>(null) : base.FindTransactionByIdempotencyKeyAsync(key, cancellationToken);
    }

    [Fact]
    public async Task KeyRaceAtCommitReplaysOrConflicts()
    {
        var hiding = new HidingStore(new InMemoryLedgerStore());
        var l = new GeneralLedger(hiding);
        foreach (string id in new[] { "a", "b", "c" })
        {
            await l.OpenAccountAsync(new Account { Id = id, Currency = Currency.BRL, Type = AccountType.Asset });
        }

        var request = new PostRequest { IdempotencyKey = "k", Postings = Legs(Dr("a", Brl("1")), Cr("b", Brl("1"))) };
        Transaction first = await l.PostAsync(request);
        hiding.Hides = 1;
        Transaction again = await l.PostAsync(request);
        Assert.Equal(first.Id, again.Id);

        hiding.Hides = 1;
        var other = new PostRequest { IdempotencyKey = "k", Postings = Legs(Dr("a", Brl("1")), Cr("c", Brl("1"))) };
        await RejectsAsync(LedgerError.IdempotencyConflict, () => l.PostAsync(other));

        hiding.Hides = 2; // the retry also misses: the store error surfaces
        await RejectsAsync(LedgerError.DuplicateIdempotencyKey, () => l.PostAsync(request));
    }

    [Fact]
    public async Task FrozenAccountsRejectPostingsButReleaseHolds()
    {
        var (l, _) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "100.00");
        Transaction hold = await l.HoldAsync(new PostRequest { Postings = Legs(Dr("wallet:alice", Brl("10")), Cr("wallet:bob", Brl("10"))) });
        await l.FreezeAsync("wallet:alice");
        Account alice = await l.GetAccountAsync("wallet:alice");
        Assert.Equal(AccountFlags.Frozen | AccountFlags.NoOverdraft, alice.Flags);

        var pay = new PostRequest { Postings = Legs(Dr("wallet:alice", Brl("1")), Cr("wallet:bob", Brl("1"))) };
        await RejectsAsync(LedgerError.AccountFrozen, () => l.PostAsync(pay));
        var inbound = new PostRequest { Postings = Legs(Dr("asset:settlement", Brl("1")), Cr("wallet:alice", Brl("1"))) };
        await RejectsAsync(LedgerError.AccountFrozen, () => l.PostAsync(inbound));
        await RejectsAsync(LedgerError.AccountFrozen, () => l.PostPendingAsync(new ResolveRequest { Id = hold.Id }));
        await l.VoidPendingAsync(new ResolveRequest { Id = hold.Id });

        await l.UnfreezeAsync("wallet:alice");
        await l.PostAsync(pay);
        await RejectsAsync(LedgerError.AccountNotFound, () => l.FreezeAsync("nope"));
        await AuditAsync(l);
    }

    [Fact]
    public async Task TwoPhaseTransfers()
    {
        var (l, _) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "100.00");
        await RejectsAsync(LedgerError.InvalidPostings, () => l.HoldAsync(new PostRequest
        {
            Timeout = TimeSpan.FromTicks(-1),
            Postings = Legs(Dr("wallet:alice", Brl("1")), Cr("wallet:bob", Brl("1"))),
        }));
        await RejectsAsync(LedgerError.InvalidPostings, () => l.HoldAsync(new PostRequest { Postings = Legs(Dr("wallet:alice", Brl("1"))) }));

        // R$ 60,00 to Bob plus a R$ 0,90 fee, held.
        Transaction hold = await l.HoldAsync(new PostRequest
        {
            Postings = Legs(Dr("wallet:alice", Brl("60.90")), Cr("wallet:bob", Brl("60.00")), Cr("revenue:fees", Brl("0.90"))),
        });
        await WantAvailableAsync(l, "wallet:alice", "100.00", "39.10");

        // Incoming holds are not spendable.
        Balance bob = await l.GetBalanceAsync("wallet:bob");
        Assert.Equal(Brl("60.00"), bob.Pending);
        Assert.True(bob.Available.IsZero);

        // Holds count against NoOverdraft.
        await RejectsAsync(LedgerError.InsufficientFunds, () => l.PostAsync(new PostRequest
        {
            Postings = Legs(Dr("wallet:alice", Brl("39.11")), Cr("wallet:bob", Brl("39.11"))),
        }));

        foreach ((Money amount, LedgerError error) in new[]
        {
            (Brl("60.91"), LedgerError.ExceedsPending),
            (new Money(1, Currency.USD), LedgerError.ExceedsPending),
            (Brl("0"), LedgerError.InvalidPostings),
        })
        {
            await RejectsAsync(error, () => l.PostPendingAsync(new ResolveRequest { Id = hold.Id, Amount = amount }));
        }

        await RejectsAsync(LedgerError.InvalidPostings, () => l.VoidPendingAsync(new ResolveRequest { Id = hold.Id, Amount = bob.Posted }));
        await RejectsAsync(LedgerError.TransactionNotFound, () => l.PostPendingAsync(new ResolveRequest { Id = "missing" }));

        // Post half: legs scale proportionally, fee included.
        Transaction post = await l.PostPendingAsync(new ResolveRequest { Id = hold.Id, Amount = Brl("30.45") });
        Assert.Equal(
            new[] { "debit wallet:alice BRL 30.45", "credit wallet:bob BRL 30.00", "credit revenue:fees BRL 0.45" },
            post.Postings.Select(p => p.ToString()));
        Assert.Equal(TransactionKind.PostPending, post.Kind);
        Assert.Equal(hold.Id, post.Ref);
        await WantAvailableAsync(l, "wallet:alice", "69.55", "69.55");
        await WantAvailableAsync(l, "wallet:bob", "30.00", "30.00");
        await WantAvailableAsync(l, "revenue:fees", "0.45", "0.45");

        await RejectsAsync(LedgerError.NotPending, () => l.PostPendingAsync(new ResolveRequest { Id = hold.Id }));
        await RejectsAsync(LedgerError.TransactionNotFound, () => l.PostPendingAsync(new ResolveRequest { Id = post.Id }));

        // A partial post that rounds a leg to zero drops it.
        Transaction h2 = await l.HoldAsync(new PostRequest
        {
            Postings = Legs(Dr("wallet:alice", Brl("10.00")), Cr("wallet:bob", Brl("9.99")), Cr("revenue:fees", Brl("0.01"))),
        });
        Transaction p2 = await l.PostPendingAsync(new ResolveRequest { Id = h2.Id, Amount = Brl("0.01") });
        Assert.Equal(2, p2.Postings.Count);

        Transaction h3 = await l.HoldAsync(new PostRequest { Postings = Legs(Dr("wallet:alice", Brl("5")), Cr("wallet:bob", Brl("5"))) });
        Transaction p3 = await l.PostPendingAsync(new ResolveRequest { Id = h3.Id, Amount = Brl("5") });
        Assert.Equal(Brl("5"), p3.Postings[0].Amount);
        Transaction h4 = await l.HoldAsync(new PostRequest { Postings = Legs(Dr("wallet:alice", Brl("5")), Cr("wallet:bob", Brl("5"))) });
        Transaction p4 = await l.PostPendingAsync(new ResolveRequest { Id = h4.Id });
        Assert.Equal(2, p4.Postings.Count);
        await AuditAsync(l);
    }

    [Fact]
    public async Task PartialPostOfMultiCurrencyHoldIsRejected()
    {
        var (l, _) = await BankAsync();
        await l.OpenAccountAsync(new Account { Id = "usd:a", Currency = Currency.USD, Type = AccountType.Asset });
        await l.OpenAccountAsync(new Account { Id = "usd:b", Currency = Currency.USD, Type = AccountType.Asset });
        Transaction h = await l.HoldAsync(new PostRequest
        {
            Postings = Legs(
                Dr("asset:settlement", Brl("1")),
                Cr("payable:partner", Brl("1")),
                Dr("usd:a", new Money(1, Currency.USD)),
                Cr("usd:b", new Money(1, Currency.USD))),
        });
        await RejectsAsync(LedgerError.ExceedsPending, () => l.PostPendingAsync(new ResolveRequest { Id = h.Id, Amount = Brl("0.50") }));
        await l.PostPendingAsync(new ResolveRequest { Id = h.Id });
        await AuditAsync(l);
    }

    [Fact]
    public async Task ExpirySweepsDueHolds()
    {
        var (l, clock) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "100.00");
        var holds = new List<Transaction>();
        TimeSpan[] timeouts = { TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.Zero };
        for (int i = 0; i < timeouts.Length; i++)
        {
            holds.Add(await l.HoldAsync(new PostRequest
            {
                Description = "hold " + i,
                Postings = Legs(Dr("wallet:alice", Brl("10")), Cr("wallet:bob", Brl("10"))),
                Timeout = timeouts[i],
            }));
        }

        Assert.Null(holds[2].ExpiresAt);
        clock.Advance(TimeSpan.FromMinutes(90));
        await l.FreezeAsync("wallet:alice");
        Assert.Equal(1, await l.ExpirePendingAsync());
        PendingTransfer p = await l.GetPendingAsync(holds[0].Id);
        Assert.Equal(PendingStatus.Expired, p.Status);
        Assert.NotNull(p.ResolvedBy);
        Transaction expiry = await l.GetTransactionAsync(p.ResolvedBy!);
        Assert.Equal(TransactionKind.ExpirePending, expiry.Kind);
        await l.UnfreezeAsync("wallet:alice");

        // Not yet due.
        await l.PostPendingAsync(new ResolveRequest { Id = holds[1].Id, Amount = Brl("1") });
        clock.Advance(TimeSpan.FromHours(10));
        Assert.Equal(0, await l.ExpirePendingAsync());
        await WantAvailableAsync(l, "wallet:alice", "99.00", "89.00");
        await AuditAsync(l);
    }

    [Fact]
    public async Task ExpirySweepPagesThroughManyHolds()
    {
        var (l, clock) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "1000.00");
        for (int i = 0; i < 300; i++)
        {
            await l.HoldAsync(new PostRequest { Timeout = TimeSpan.FromMinutes(1), Postings = Legs(Dr("wallet:alice", Brl("1")), Cr("wallet:bob", Brl("1"))) });
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(300, await l.ExpirePendingAsync());
        await WantAvailableAsync(l, "wallet:alice", "1000.00", "1000.00");
    }

    [Fact]
    public async Task ReversalsMirrorAndRespectInvariants()
    {
        var (l, _) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "100.00");
        Transaction pay = await l.PostAsync(new PostRequest { Postings = Legs(Dr("wallet:alice", Brl("40")), Cr("wallet:bob", Brl("40"))) });
        Transaction hold = await l.HoldAsync(new PostRequest { Postings = Legs(Dr("wallet:alice", Brl("10")), Cr("wallet:bob", Brl("10"))) });
        Transaction voided = await l.VoidPendingAsync(new ResolveRequest { Id = hold.Id });

        foreach (string id in new[] { hold.Id, voided.Id })
        {
            await RejectsAsync(LedgerError.NotReversible, () => l.ReverseAsync(new ResolveRequest { Id = id }));
        }

        await RejectsAsync(LedgerError.TransactionNotFound, () => l.ReverseAsync(new ResolveRequest { Id = "missing" }));
        await RejectsAsync(LedgerError.InvalidPostings, () => l.ReverseAsync(new ResolveRequest { Id = pay.Id, Amount = Brl("1") }));

        // Bob spent the money: the reversal must not overdraw him.
        await l.PostAsync(new PostRequest { Postings = Legs(Dr("wallet:bob", Brl("35")), Cr("asset:settlement", Brl("35"))) });
        await RejectsAsync(LedgerError.InsufficientFunds, () => l.ReverseAsync(new ResolveRequest { Id = pay.Id }));
        await DepositAsync(l, "wallet:bob", "35.00");
        Transaction rev = await l.ReverseAsync(new ResolveRequest { Id = pay.Id });
        await RejectsAsync(LedgerError.AlreadyReversed, () => l.ReverseAsync(new ResolveRequest { Id = pay.Id }));
        await RejectsAsync(LedgerError.NotReversible, () => l.ReverseAsync(new ResolveRequest { Id = rev.Id }));

        // Posted pending transfers are reversible.
        Transaction h2 = await l.HoldAsync(new PostRequest { Postings = Legs(Dr("wallet:alice", Brl("10")), Cr("wallet:bob", Brl("10"))) });
        Transaction p2 = await l.PostPendingAsync(new ResolveRequest { Id = h2.Id, Amount = Brl("4") });
        await l.ReverseAsync(new ResolveRequest { Id = p2.Id });
        await WantAvailableAsync(l, "wallet:alice", "100.00", "100.00");

        // History is intact: the original still says what it said.
        Transaction original = await l.GetTransactionAsync(pay.Id);
        Assert.Equal(pay.Hash, original.Hash);
        Assert.Equal(Direction.Debit, original.Postings[0].Direction);
        await AuditAsync(l);
    }

    [Fact]
    public async Task StatementsPaginateStablyWithRunningBalances()
    {
        var (l, clock) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "100.00");
        DateTimeOffset start = clock.GetUtcNow();
        for (int i = 0; i < 7; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await l.PostAsync(new PostRequest { Description = "p" + i, Postings = Legs(Dr("wallet:alice", Brl("1")), Cr("wallet:bob", Brl("1"))) });
        }

        Transaction h = await l.HoldAsync(new PostRequest { Description = "hold", Postings = Legs(Dr("wallet:alice", Brl("5")), Cr("wallet:bob", Brl("5"))) });
        await l.VoidPendingAsync(new ResolveRequest { Id = h.Id, Description = "void" });

        var all = new List<StatementLine>();
        var options = new StatementOptions { Limit = 3 };
        for (int pages = 0; ; pages++)
        {
            Assert.True(pages <= 10, "pagination does not terminate");
            Statement st = await l.GetStatementAsync("wallet:alice", options);
            all.AddRange(st.Lines);
            if (st.NextCursor is null)
            {
                break;
            }

            options = options with { Cursor = st.NextCursor };

            // New activity does not disturb pages already served.
            if (pages == 0)
            {
                await DepositAsync(l, "wallet:bob", "1.00");
            }
        }

        Assert.Equal(8, all.Count);
        StatementLine last = all[^1];
        Assert.Equal(Brl("93.00"), last.Balance);
        Assert.Equal(Brl("-1.00"), last.Amount);
        Assert.Equal("p6", last.Description);

        Statement withHolds = await l.GetStatementAsync("wallet:alice", new StatementOptions { IncludeHolds = true });
        Assert.Equal(10, withHolds.Lines.Count);
        Assert.Null(withHolds.NextCursor);
        StatementLine held = withHolds.Lines[8];
        StatementLine released = withHolds.Lines[9];
        Assert.Equal(EntryPhase.Held, held.Phase);
        Assert.Equal(Brl("-5"), held.Amount);
        Assert.Equal(Brl("88"), held.Available);
        Assert.Equal(EntryPhase.Released, released.Phase);
        Assert.Equal(Brl("5"), released.Amount);
        Assert.Equal(Brl("93"), released.Available);

        Assert.Equal(8, (await l.GetStatementAsync("wallet:alice", new StatementOptions { Limit = 5000 })).Lines.Count);
        await RejectsAsync(LedgerError.AccountNotFound, () => l.GetStatementAsync("nope"));
        foreach (string cursor in new[] { "!!", "AAAA", "eDEAAAAAAAAAAA", "A", "s1==" })
        {
            await RejectsAsync(LedgerError.InvalidCursor, () => l.GetStatementAsync("wallet:alice", new StatementOptions { Cursor = cursor }));
        }

        // Point-in-time balances.
        foreach ((DateTimeOffset at, string want) in new[]
        {
            (start.AddSeconds(-1), "0"),
            (start, "100"),
            (start.AddMinutes(3).AddSeconds(30), "97"),
            (start.AddHours(1), "93"),
        })
        {
            Balance b = await l.GetBalanceAtAsync("wallet:alice", at);
            Assert.Equal(Brl(want), b.Posted);
        }

        await RejectsAsync(LedgerError.AccountNotFound, () => l.GetBalanceAtAsync("nope", start));
        await RejectsAsync(LedgerError.AccountNotFound, () => l.GetBalanceAsync("nope"));
    }

    [Fact]
    public async Task TrialBalanceChecksBothPhases()
    {
        var (l, _) = await BankAsync();
        await DepositAsync(l, "wallet:alice", "10.00");
        await l.HoldAsync(new PostRequest { Postings = Legs(Dr("wallet:alice", Brl("3")), Cr("wallet:bob", Brl("3"))) });
        TrialBalance tb = await l.GetTrialBalanceAsync();
        tb.Check();
        Assert.True(tb.IsBalanced);
        Assert.Equal(5, tb.Rows.Count);
        Assert.Equal(new[] { Currency.BRL }, tb.Currencies());
        CurrencyTotals totals = tb.Totals[Currency.BRL];
        Assert.Equal(1000, totals.DebitsPosted);
        Assert.Equal(300, totals.DebitsPending);

        foreach (CurrencyTotals bad in new[] { totals with { DebitsPosted = totals.DebitsPosted + 1 }, totals with { CreditsPending = totals.CreditsPending + 1 } })
        {
            var broken = new TrialBalance(tb.Rows, new Dictionary<Currency, CurrencyTotals> { [Currency.BRL] = bad });
            Assert.False(broken.IsBalanced);
            LedgerException e = Assert.Throws<LedgerException>(broken.Check);
            Assert.Equal(LedgerError.Unbalanced, e.Code);
        }
    }

    [Fact]
    public async Task HooksFireOncePerCommitAndClockNeverGoesBack()
    {
        var committed = new List<string>();
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new ManualClock(t0);
        var l = new GeneralLedger(new InMemoryLedgerStore(), new LedgerOptions
        {
            TimeProvider = clock,
            OnCommit = new Action<Transaction>[] { t => committed.Add(t.Id) },
        });
        foreach (string id in new[] { "a", "b" })
        {
            await l.OpenAccountAsync(new Account { Id = id, Currency = Currency.JPY, Type = AccountType.Asset });
        }

        var request = new PostRequest
        {
            IdempotencyKey = "x",
            Postings = Legs(Dr("a", new Money(5, Currency.JPY)), Cr("b", new Money(5, Currency.JPY))),
        };
        Transaction first = await l.PostAsync(request);
        await l.PostAsync(request); // replay: no hook
        await RejectsAsync(LedgerError.InvalidPostings, () => l.PostAsync(new PostRequest { Postings = request.Postings.Take(1).ToArray() }));
        clock.Set(t0.AddHours(-1)); // the clock goes backwards
        Transaction second = await l.PostAsync(new PostRequest { Postings = request.Postings });
        Assert.Equal(new[] { first.Id, second.Id }, committed);
        Assert.Equal(t0, second.Timestamp);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", first.Id);

        IReadOnlyList<Transaction> tail = await l.GetTransactionsAsync(1, 10);
        Assert.Single(tail);
        Assert.Equal(second.Id, tail[0].Id);

        // Returned transactions do not alias store memory.
        Assert.Throws<NotSupportedException>(() => ((IList<Posting>)tail[0].Postings)[0] = Dr("evil", new Money(5, Currency.JPY)));
        Assert.Equal("a", (await l.GetTransactionAsync(second.Id)).Postings[0].Account);
        await RejectsAsync(LedgerError.TransactionNotFound, () => l.GetTransactionAsync("nope"));
    }

    [Fact]
    public async Task CanceledTokensStopWork()
    {
        var (l, _) = await BankAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var request = new PostRequest { Postings = Legs(Dr("asset:settlement", Brl("1")), Cr("wallet:bob", Brl("1"))) };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await l.PostAsync(request, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await l.GetBalanceAsync("wallet:bob", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await l.ExpirePendingAsync(cts.Token));
    }

    [Fact]
    public void UuidV7IsUniqueAndWellFormed()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 10_000; i++)
        {
            string id = UuidV7.NewString();
            Assert.True(seen.Add(id), "duplicate UUID " + id);
        }

        string fixedTime = UuidV7.NewString(DateTimeOffset.FromUnixTimeMilliseconds(0x0123_4567_89AB));
        Assert.StartsWith("01234567-89ab-7", fixedTime);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", fixedTime);
    }

    [Fact]
    public void NamesMatchTheGoImplementation()
    {
        var cases = new (string Got, string Want)[]
        {
            (Direction.Debit.ToCode(), "debit"),
            (Direction.Credit.ToCode(), "credit"),
            (((Direction)7).ToCode(), "Direction(7)"),
            (AccountType.Asset.ToCode(), "asset"),
            (AccountType.Liability.ToCode(), "liability"),
            (AccountType.Equity.ToCode(), "equity"),
            (AccountType.Revenue.ToCode(), "revenue"),
            (AccountType.Expense.ToCode(), "expense"),
            (((AccountType)0).ToCode(), "AccountType(0)"),
            (AccountFlags.None.ToCode(), "none"),
            ((AccountFlags.NoOverdraft | AccountFlags.Frozen | (AccountFlags)8).ToCode(), "no_overdraft|frozen|0x8"),
            (TransactionKind.Standard.ToCode(), "standard"),
            (TransactionKind.Pending.ToCode(), "pending"),
            (TransactionKind.PostPending.ToCode(), "post_pending"),
            (TransactionKind.VoidPending.ToCode(), "void_pending"),
            (TransactionKind.ExpirePending.ToCode(), "expire_pending"),
            (TransactionKind.Reversal.ToCode(), "reversal"),
            (((TransactionKind)0).ToCode(), "Kind(0)"),
            (EntryPhase.Posted.ToCode(), "posted"),
            (EntryPhase.Held.ToCode(), "held"),
            (EntryPhase.Released.ToCode(), "released"),
            (((EntryPhase)0).ToCode(), "Phase(0)"),
            (PendingStatus.Open.ToCode(), "open"),
            (PendingStatus.Posted.ToCode(), "posted"),
            (PendingStatus.Voided.ToCode(), "voided"),
            (PendingStatus.Expired.ToCode(), "expired"),
            (((PendingStatus)0).ToCode(), "PendingStatus(0)"),
            (Direction.Debit.Opposite().ToCode(), "credit"),
            (Direction.Credit.Opposite().ToCode(), "debit"),
            (new ChainBrokenException(3, "t", "r").Message, "audit chain broken: at seq 3 (tx t): r"),
        };
        foreach ((string got, string want) in cases)
        {
            Assert.Equal(want, got);
        }
    }
}
