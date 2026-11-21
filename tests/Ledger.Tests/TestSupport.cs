using System.Globalization;
using Ledger.InMemory;
using Ledger.Monetary;

namespace Ledger.Tests;

/// <summary>A manual clock for reproducible tests.</summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now;

    public ManualClock(DateTimeOffset start)
    {
        _now = start;
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            _now += by;
        }
    }

    public void Set(DateTimeOffset now)
    {
        lock (_gate)
        {
            _now = now;
        }
    }
}

/// <summary>Readable, sequential transaction identifiers: tx-001, tx-002, ...</summary>
internal sealed class SequentialIds
{
    private int _n;

    public string Next() => "tx-" + Interlocked.Increment(ref _n).ToString("000", CultureInfo.InvariantCulture);
}

internal static class TestSupport
{
    public static readonly DateTimeOffset Start = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    public static Money Brl(string amount) => Money.Parse(Currency.BRL, amount);

    public static Posting Dr(string account, Money amount) => Posting.Debit(account, amount);

    public static Posting Cr(string account, Money amount) => Posting.Credit(account, amount);

    public static Posting[] Legs(params Posting[] postings) => postings;

    /// <summary>
    /// A small chart of accounts: the settlement account at the central bank
    /// (asset), two customer wallets that cannot go negative (liabilities), a
    /// partner payable and fee revenue.
    /// </summary>
    public static async Task<(GeneralLedger Ledger, ManualClock Clock)> BankAsync(ILedgerStore? store = null)
    {
        var clock = new ManualClock(Start);
        var ids = new SequentialIds();
        var ledger = new GeneralLedger(store ?? new InMemoryLedgerStore(), new LedgerOptions { TimeProvider = clock, IdGenerator = ids.Next });
        Account[] chart =
        {
            new() { Id = "asset:settlement", Code = "1.1.01", Currency = Currency.BRL, Type = AccountType.Asset },
            new() { Id = "wallet:alice", Code = "2.1.01", Currency = Currency.BRL, Type = AccountType.Liability, Flags = AccountFlags.NoOverdraft },
            new() { Id = "wallet:bob", Code = "2.1.02", Currency = Currency.BRL, Type = AccountType.Liability, Flags = AccountFlags.NoOverdraft },
            new() { Id = "payable:partner", Code = "2.2.01", Currency = Currency.BRL, Type = AccountType.Liability },
            new() { Id = "revenue:fees", Code = "3.1.01", Currency = Currency.BRL, Type = AccountType.Revenue },
        };
        foreach (Account a in chart)
        {
            await ledger.OpenAccountAsync(a);
        }

        return (ledger, clock);
    }

    public static async Task DepositAsync(GeneralLedger ledger, string to, string amount)
    {
        await ledger.PostAsync(new PostRequest
        {
            IdempotencyKey = "deposit-" + to,
            Description = "Pix in",
            Postings = Legs(Dr("asset:settlement", Brl(amount)), Cr(to, Brl(amount))),
        });
    }

    public static async Task WantAvailableAsync(GeneralLedger ledger, string account, string posted, string available)
    {
        Balance b = await ledger.GetBalanceAsync(account);
        Assert.Equal(Brl(posted), b.Posted);
        Assert.Equal(Brl(available), b.Available);
    }

    /// <summary>Trial balance, audit chain and balance replay must all pass.</summary>
    public static async Task AuditAsync(GeneralLedger ledger)
    {
        TrialBalance tb = await ledger.GetTrialBalanceAsync();
        tb.Check();
        await ledger.VerifyChainAsync();
        await ledger.VerifyBalancesAsync();
    }

    public static async Task<LedgerException> RejectsAsync<T>(LedgerError expected, Func<ValueTask<T>> action)
    {
        LedgerException e = await Assert.ThrowsAnyAsync<LedgerException>(async () => await action());
        Assert.Equal(expected, e.Code);
        return e;
    }

    public static async Task<LedgerException> RejectsAsync(LedgerError expected, Func<ValueTask> action)
    {
        LedgerException e = await Assert.ThrowsAnyAsync<LedgerException>(async () => await action());
        Assert.Equal(expected, e.Code);
        return e;
    }

    public static Sha256Hash FlipBit(Sha256Hash hash, int index)
    {
        byte[] bytes = hash.ToArray();
        bytes[index] ^= 1;
        return Sha256Hash.FromBytes(bytes);
    }
}

/// <summary>Forwards every call to an inner store; tests override what they need to sabotage.</summary>
internal class DelegatingStore : ILedgerStore
{
    public DelegatingStore(ILedgerStore inner)
    {
        Inner = inner;
    }

    protected ILedgerStore Inner { get; }

    public virtual ValueTask UpdateAsync(IReadOnlyList<string> lockAccounts, Func<IStoreTransaction, CancellationToken, ValueTask<bool>> work, CancellationToken cancellationToken = default) =>
        Inner.UpdateAsync(lockAccounts, work, cancellationToken);

    public virtual ValueTask<T> ViewAsync<T>(Func<IStoreReader, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken = default) =>
        Inner.ViewAsync(read, cancellationToken);
}

/// <summary>Forwards every read to an inner reader.</summary>
internal class DelegatingReader : IStoreReader
{
    public DelegatingReader(IStoreReader inner)
    {
        InnerReader = inner;
    }

    protected IStoreReader InnerReader { get; }

    public virtual ValueTask<Account?> FindAccountAsync(string id, CancellationToken cancellationToken) => InnerReader.FindAccountAsync(id, cancellationToken);

    public virtual ValueTask<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken) => InnerReader.GetAccountsAsync(cancellationToken);

    public virtual ValueTask<Balances?> FindBalancesAsync(string id, CancellationToken cancellationToken) => InnerReader.FindBalancesAsync(id, cancellationToken);

    public virtual ValueTask<Transaction?> FindTransactionAsync(string id, CancellationToken cancellationToken) => InnerReader.FindTransactionAsync(id, cancellationToken);

    public virtual ValueTask<Transaction?> FindTransactionByIdempotencyKeyAsync(string key, CancellationToken cancellationToken) =>
        InnerReader.FindTransactionByIdempotencyKeyAsync(key, cancellationToken);

    public virtual ValueTask<IReadOnlyList<Transaction>> GetTransactionsAsync(long afterSeq, int limit, CancellationToken cancellationToken) =>
        InnerReader.GetTransactionsAsync(afterSeq, limit, cancellationToken);

    public virtual ValueTask<IReadOnlyList<Entry>> GetEntriesAsync(string accountId, long afterAccountSeq, int limit, CancellationToken cancellationToken) =>
        InnerReader.GetEntriesAsync(accountId, afterAccountSeq, limit, cancellationToken);

    public virtual ValueTask<Entry?> FindEntryAtAsync(string accountId, DateTimeOffset at, CancellationToken cancellationToken) =>
        InnerReader.FindEntryAtAsync(accountId, at, cancellationToken);

    public virtual ValueTask<PendingTransfer?> FindPendingAsync(string id, CancellationToken cancellationToken) => InnerReader.FindPendingAsync(id, cancellationToken);

    public virtual ValueTask<IReadOnlyList<PendingTransfer>> GetExpiredPendingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        InnerReader.GetExpiredPendingAsync(now, limit, cancellationToken);

    public virtual ValueTask<string?> FindReversalAsync(string id, CancellationToken cancellationToken) => InnerReader.FindReversalAsync(id, cancellationToken);
}

/// <summary>Forwards every call to an inner store transaction.</summary>
internal class DelegatingTransaction : DelegatingReader, IStoreTransaction
{
    public DelegatingTransaction(IStoreTransaction inner)
        : base(inner)
    {
        InnerTransaction = inner;
    }

    protected IStoreTransaction InnerTransaction { get; }

    public virtual ValueTask<ChainHead> GetChainHeadAsync(CancellationToken cancellationToken) => InnerTransaction.GetChainHeadAsync(cancellationToken);

    public virtual ValueTask CreateAccountAsync(Account account, CancellationToken cancellationToken) => InnerTransaction.CreateAccountAsync(account, cancellationToken);

    public virtual ValueTask UpdateAccountAsync(Account account, CancellationToken cancellationToken) => InnerTransaction.UpdateAccountAsync(account, cancellationToken);

    public virtual ValueTask PutBalancesAsync(string accountId, Balances balances, CancellationToken cancellationToken) =>
        InnerTransaction.PutBalancesAsync(accountId, balances, cancellationToken);

    public virtual ValueTask AppendTransactionAsync(Transaction transaction, IReadOnlyList<Entry> entries, CancellationToken cancellationToken) =>
        InnerTransaction.AppendTransactionAsync(transaction, entries, cancellationToken);

    public virtual ValueTask PutPendingAsync(PendingTransfer pending, CancellationToken cancellationToken) => InnerTransaction.PutPendingAsync(pending, cancellationToken);
}
