namespace Ledger;

/// <summary>
/// Persists ledger state with transactional semantics. The ledger owns every
/// business rule; a store only provides atomic, isolated read-modify-write
/// over a set of locked accounts and consistent snapshots for reads.
/// </summary>
/// <remarks>
/// The shape maps directly onto SQL: an update is <c>BEGIN; SELECT ... FROM
/// account_balances WHERE account_id = ANY($1) ORDER BY account_id FOR UPDATE;
/// work; COMMIT</c>.
/// </remarks>
public interface ILedgerStore
{
    /// <summary>
    /// Locks the given accounts (exclusively, in ascending identifier order;
    /// duplicates allowed) and runs <paramref name="work"/>. If it returns
    /// true, every write made through the <see cref="IStoreTransaction"/>
    /// becomes visible atomically; if it returns false or throws, none does
    /// and the exception propagates. Implementations may retry the work on
    /// serialization failures, so it must have no side effects other than
    /// through the transaction.
    /// </summary>
    /// <param name="lockAccounts">The accounts the work may read balances of and write.</param>
    /// <param name="work">The work; returns whether to commit.</param>
    /// <param name="cancellationToken">Cancels waiting for locks.</param>
    /// <returns>A task that completes when the update has committed or rolled back.</returns>
    ValueTask UpdateAsync(
        IReadOnlyList<string> lockAccounts,
        Func<IStoreTransaction, CancellationToken, ValueTask<bool>> work,
        CancellationToken cancellationToken = default);

    /// <summary>Runs <paramref name="read"/> against a consistent snapshot.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="read">The read.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The read's result.</returns>
    ValueTask<T> ViewAsync<T>(Func<IStoreReader, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken = default);
}

/// <summary>The read side of a store. Lookups return null when nothing is found. Returned values must not alias mutable store memory.</summary>
public interface IStoreReader
{
    /// <summary>Returns one account, or null.</summary>
    /// <param name="id">The account identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The account, or null.</returns>
    ValueTask<Account?> FindAccountAsync(string id, CancellationToken cancellationToken);

    /// <summary>Returns all accounts in ascending identifier order (byte-wise on UTF-8).</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The accounts.</returns>
    ValueTask<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken);

    /// <summary>Returns an account's counters, or null if it does not exist. Inside an update the account must be locked (<see cref="LedgerError.NotLocked"/> otherwise).</summary>
    /// <param name="id">The account identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The counters, or null.</returns>
    ValueTask<Balances?> FindBalancesAsync(string id, CancellationToken cancellationToken);

    /// <summary>Returns a transaction by identifier, or null.</summary>
    /// <param name="id">The transaction identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The transaction, or null.</returns>
    ValueTask<Transaction?> FindTransactionAsync(string id, CancellationToken cancellationToken);

    /// <summary>Returns the transaction recorded under an idempotency key, or null.</summary>
    /// <param name="key">The idempotency key.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The transaction, or null.</returns>
    ValueTask<Transaction?> FindTransactionByIdempotencyKeyAsync(string key, CancellationToken cancellationToken);

    /// <summary>Returns up to <paramref name="limit"/> transactions with a sequence number greater than <paramref name="afterSeq"/>, in order.</summary>
    /// <param name="afterSeq">The last sequence number already seen (0 for the start).</param>
    /// <param name="limit">The maximum number of transactions.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The transactions.</returns>
    ValueTask<IReadOnlyList<Transaction>> GetTransactionsAsync(long afterSeq, int limit, CancellationToken cancellationToken);

    /// <summary>Returns up to <paramref name="limit"/> entries of an account with an account sequence number greater than <paramref name="afterAccountSeq"/>, in order.</summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="afterAccountSeq">The last account sequence number already seen (0 for the start).</param>
    /// <param name="limit">The maximum number of entries.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The entries.</returns>
    ValueTask<IReadOnlyList<Entry>> GetEntriesAsync(string accountId, long afterAccountSeq, int limit, CancellationToken cancellationToken);

    /// <summary>Returns the account's last entry with a time at or before <paramref name="at"/>, or null if there is none.</summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="at">The point in time, inclusive.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The entry, or null.</returns>
    ValueTask<Entry?> FindEntryAtAsync(string accountId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Returns the state record of a pending transfer, or null.</summary>
    /// <param name="id">The pending transaction identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The record, or null.</returns>
    ValueTask<PendingTransfer?> FindPendingAsync(string id, CancellationToken cancellationToken);

    /// <summary>Returns up to <paramref name="limit"/> open pending transfers with an expiry at or before <paramref name="now"/>, oldest expiry first (ties by identifier).</summary>
    /// <param name="now">The current time.</param>
    /// <param name="limit">The maximum number of records.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The records.</returns>
    ValueTask<IReadOnlyList<PendingTransfer>> GetExpiredPendingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);

    /// <summary>Returns the identifier of the reversal of a transaction, or null.</summary>
    /// <param name="id">The original transaction identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The reversal's identifier, or null.</returns>
    ValueTask<string?> FindReversalAsync(string id, CancellationToken cancellationToken);
}

/// <summary>The read-write view inside <see cref="ILedgerStore.UpdateAsync"/>. Reads observe the update's own writes.</summary>
public interface IStoreTransaction : IStoreReader
{
    /// <summary>Returns the audit chain tip and holds it until the update ends, so that exactly one writer can append next (<c>SELECT ... FOR UPDATE</c> on the single chain_head row).</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The chain head.</returns>
    ValueTask<ChainHead> GetChainHeadAsync(CancellationToken cancellationToken);

    /// <summary>Inserts a new, locked account with zero balances (<see cref="LedgerError.AccountExists"/> if the identifier is taken).</summary>
    /// <param name="account">The account.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the write is buffered.</returns>
    ValueTask CreateAccountAsync(Account account, CancellationToken cancellationToken);

    /// <summary>Replaces a locked account's mutable fields (flags, name, code).</summary>
    /// <param name="account">The account.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the write is buffered.</returns>
    ValueTask UpdateAccountAsync(Account account, CancellationToken cancellationToken);

    /// <summary>Replaces a locked account's counters.</summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="balances">The new counters.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the write is buffered.</returns>
    ValueTask PutBalancesAsync(string accountId, Balances balances, CancellationToken cancellationToken);

    /// <summary>Records a sealed transaction and its entries and advances the chain head. A store may detect a taken idempotency key only at commit (<see cref="LedgerError.DuplicateIdempotencyKey"/>).</summary>
    /// <param name="transaction">The sealed transaction.</param>
    /// <param name="entries">Its entries.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the write is buffered.</returns>
    ValueTask AppendTransactionAsync(Transaction transaction, IReadOnlyList<Entry> entries, CancellationToken cancellationToken);

    /// <summary>Inserts or replaces a pending transfer's state record.</summary>
    /// <param name="pending">The record.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the write is buffered.</returns>
    ValueTask PutPendingAsync(PendingTransfer pending, CancellationToken cancellationToken);
}
