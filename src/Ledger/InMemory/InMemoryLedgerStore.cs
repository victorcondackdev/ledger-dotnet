using System.Collections.Concurrent;
using System.Collections.Immutable;
using Ledger.Internal;

namespace Ledger.InMemory;

/// <summary>
/// A concurrency-safe, in-memory <see cref="ILedgerStore"/>: the reference
/// implementation of the store contract, fast enough for tests, simulations
/// and single-process services that snapshot elsewhere. It is not durable.
/// </summary>
/// <remarks>
/// <para>
/// Committed state is an immutable snapshot that a commit replaces
/// atomically, so readers never block writers and a view always observes
/// whole transactions: a trial balance computed in a view sums to zero even
/// while transfers are running.
/// </para>
/// <para>
/// Writers take up to three kinds of locks, always in this order and
/// released in reverse:
/// </para>
/// <list type="number">
/// <item><description>One <see cref="SemaphoreSlim"/> per account named in the update, sorted by identifier and deduplicated. Every writer acquires them in the same global order, so no cycle of waiters can form (no deadlocks: transfers A to B and B to A serialize), and updates on disjoint accounts proceed in parallel.</description></item>
/// <item><description>The chain-head semaphore, taken lazily when the ledger asks for the chain head and held to the end of the update. It serializes appends to the audit chain, the one global critical section, kept to sealing and publishing.</description></item>
/// <item><description>A short monitor around publishing the new snapshot, with no awaits inside it.</description></item>
/// </list>
/// <para>Writes inside an update are buffered and published only on commit, so a failed update leaves no trace.</para>
/// </remarks>
public sealed class InMemoryLedgerStore : ILedgerStore, IDisposable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _accountLocks = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _chainLock = new(1, 1);
    private readonly object _publishGate = new();
    private volatile Snapshot _state = Snapshot.Empty;

    /// <inheritdoc/>
    public async ValueTask UpdateAsync(
        IReadOnlyList<string> lockAccounts,
        Func<IStoreTransaction, CancellationToken, ValueTask<bool>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lockAccounts);
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();

        var ids = new SortedSet<string>(Utf8Order.Comparer);
        foreach (string id in lockAccounts)
        {
            ArgumentNullException.ThrowIfNull(id, nameof(lockAccounts));
            ids.Add(id);
        }

        var held = new List<SemaphoreSlim>(ids.Count);
        try
        {
            foreach (string id in ids)
            {
                SemaphoreSlim gate = _accountLocks.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                held.Add(gate);
            }

            var scope = new UpdateScope(this, ids);
            try
            {
                if (await work(scope, cancellationToken).ConfigureAwait(false))
                {
                    scope.Commit();
                }
            }
            finally
            {
                scope.Close();
            }
        }
        finally
        {
            for (int i = held.Count - 1; i >= 0; i--)
            {
                held[i].Release();
            }
        }
    }

    /// <summary>Releases the lock objects. The store must not be in use.</summary>
    public void Dispose()
    {
        _chainLock.Dispose();
        foreach (SemaphoreSlim gate in _accountLocks.Values)
        {
            gate.Dispose();
        }
    }

    /// <inheritdoc/>
    public ValueTask<T> ViewAsync<T>(Func<IStoreReader, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(read);
        cancellationToken.ThrowIfCancellationRequested();
        return read(new SnapshotReader(_state), cancellationToken);
    }

    /// <summary>Immutable committed state.</summary>
    private sealed record Snapshot(
        ImmutableDictionary<string, Account> Accounts,
        ImmutableDictionary<string, Balances> Balances,
        ImmutableDictionary<string, ImmutableList<Entry>> Entries,
        ImmutableList<Transaction> Transactions,
        ImmutableDictionary<string, long> ById,
        ImmutableDictionary<string, long> ByKey,
        ImmutableDictionary<string, PendingTransfer> Pending,
        ImmutableDictionary<string, string> Reversals,
        ChainHead Head)
    {
        public static readonly Snapshot Empty = new(
            ImmutableDictionary.Create<string, Account>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, Balances>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, ImmutableList<Entry>>(StringComparer.Ordinal),
            ImmutableList<Transaction>.Empty,
            ImmutableDictionary.Create<string, long>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, long>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, PendingTransfer>(StringComparer.Ordinal),
            ImmutableDictionary.Create<string, string>(StringComparer.Ordinal),
            default);
    }

    /// <summary>Reads one committed snapshot.</summary>
    private sealed class SnapshotReader : IStoreReader
    {
        private readonly Snapshot _s;

        public SnapshotReader(Snapshot s)
        {
            _s = s;
        }

        public ValueTask<Account?> FindAccountAsync(string id, CancellationToken cancellationToken) =>
            ValueTask.FromResult(FindAccount(_s, id));

        public ValueTask<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(SortedAccounts(_s.Accounts.Values));

        public ValueTask<Balances?> FindBalancesAsync(string id, CancellationToken cancellationToken) =>
            ValueTask.FromResult(FindBalances(_s, id));

        public ValueTask<Transaction?> FindTransactionAsync(string id, CancellationToken cancellationToken) =>
            ValueTask.FromResult(FindTransaction(_s, id));

        public ValueTask<Transaction?> FindTransactionByIdempotencyKeyAsync(string key, CancellationToken cancellationToken) =>
            ValueTask.FromResult(FindByKey(_s, key));

        public ValueTask<IReadOnlyList<Transaction>> GetTransactionsAsync(long afterSeq, int limit, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Range(_s.Transactions, afterSeq, limit));

        public ValueTask<IReadOnlyList<Entry>> GetEntriesAsync(string accountId, long afterAccountSeq, int limit, CancellationToken cancellationToken) =>
            ValueTask.FromResult(EntriesOf(_s, accountId, afterAccountSeq, limit));

        public ValueTask<Entry?> FindEntryAtAsync(string accountId, DateTimeOffset at, CancellationToken cancellationToken) =>
            ValueTask.FromResult(EntryAt(_s, accountId, at));

        public ValueTask<PendingTransfer?> FindPendingAsync(string id, CancellationToken cancellationToken) =>
            ValueTask.FromResult(FindPending(_s, id));

        public ValueTask<IReadOnlyList<PendingTransfer>> GetExpiredPendingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ExpiredPending(_s, now, limit));

        public ValueTask<string?> FindReversalAsync(string id, CancellationToken cancellationToken) =>
            ValueTask.FromResult(FindReversal(_s, id));
    }

    /// <summary>Buffers the writes of one update; reads see the update's own writes over the latest committed state.</summary>
    private sealed class UpdateScope : IStoreTransaction
    {
        private readonly InMemoryLedgerStore _store;
        private readonly SortedSet<string> _locked;
        private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);
        private readonly HashSet<string> _created = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Balances> _balances = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PendingTransfer> _pending = new(StringComparer.Ordinal);
        private readonly List<Transaction> _transactions = new();
        private readonly List<IReadOnlyList<Entry>> _entries = new();
        private bool _chainHeld;
        private bool _closed;

        public UpdateScope(InMemoryLedgerStore store, SortedSet<string> locked)
        {
            _store = store;
            _locked = locked;
        }

        private Snapshot Committed => _store._state;

        public ValueTask<Account?> FindAccountAsync(string id, CancellationToken cancellationToken)
        {
            EnsureOpen();
            return ValueTask.FromResult(_accounts.TryGetValue(id, out Account? a) ? a : FindAccount(Committed, id));
        }

        public ValueTask<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken)
        {
            EnsureOpen();
            var merged = new Dictionary<string, Account>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Account> kv in Committed.Accounts)
            {
                merged[kv.Key] = kv.Value;
            }

            foreach (KeyValuePair<string, Account> kv in _accounts)
            {
                merged[kv.Key] = kv.Value;
            }

            return ValueTask.FromResult(SortedAccounts(merged.Values));
        }

        public ValueTask<Balances?> FindBalancesAsync(string id, CancellationToken cancellationToken)
        {
            EnsureOpen();
            EnsureLocked(id);
            return ValueTask.FromResult(_balances.TryGetValue(id, out Balances b) ? b : FindBalances(Committed, id));
        }

        public ValueTask<Transaction?> FindTransactionAsync(string id, CancellationToken cancellationToken)
        {
            EnsureOpen();
            foreach (Transaction t in _transactions)
            {
                if (t.Id == id)
                {
                    return ValueTask.FromResult<Transaction?>(t);
                }
            }

            return ValueTask.FromResult(FindTransaction(Committed, id));
        }

        public ValueTask<Transaction?> FindTransactionByIdempotencyKeyAsync(string key, CancellationToken cancellationToken)
        {
            EnsureOpen();
            foreach (Transaction t in _transactions)
            {
                if (!string.IsNullOrEmpty(key) && t.IdempotencyKey == key)
                {
                    return ValueTask.FromResult<Transaction?>(t);
                }
            }

            return ValueTask.FromResult(FindByKey(Committed, key));
        }

        public ValueTask<IReadOnlyList<Transaction>> GetTransactionsAsync(long afterSeq, int limit, CancellationToken cancellationToken)
        {
            EnsureOpen();
            return ValueTask.FromResult(Range(Committed.Transactions, afterSeq, limit));
        }

        public ValueTask<IReadOnlyList<Entry>> GetEntriesAsync(string accountId, long afterAccountSeq, int limit, CancellationToken cancellationToken)
        {
            EnsureOpen();
            return ValueTask.FromResult(EntriesOf(Committed, accountId, afterAccountSeq, limit));
        }

        public ValueTask<Entry?> FindEntryAtAsync(string accountId, DateTimeOffset at, CancellationToken cancellationToken)
        {
            EnsureOpen();
            return ValueTask.FromResult(EntryAt(Committed, accountId, at));
        }

        public ValueTask<PendingTransfer?> FindPendingAsync(string id, CancellationToken cancellationToken)
        {
            EnsureOpen();
            return ValueTask.FromResult(_pending.TryGetValue(id, out PendingTransfer? p) ? p : FindPending(Committed, id));
        }

        public ValueTask<IReadOnlyList<PendingTransfer>> GetExpiredPendingAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken)
        {
            EnsureOpen();
            return ValueTask.FromResult(ExpiredPending(Committed, now, limit));
        }

        public ValueTask<string?> FindReversalAsync(string id, CancellationToken cancellationToken)
        {
            EnsureOpen();
            foreach (Transaction t in _transactions)
            {
                if (t.Kind == TransactionKind.Reversal && t.Ref == id)
                {
                    return ValueTask.FromResult<string?>(t.Id);
                }
            }

            return ValueTask.FromResult(FindReversal(Committed, id));
        }

        public async ValueTask<ChainHead> GetChainHeadAsync(CancellationToken cancellationToken)
        {
            EnsureOpen();
            if (!_chainHeld)
            {
                await _store._chainLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                _chainHeld = true;
            }

            if (_transactions.Count > 0)
            {
                Transaction last = _transactions[_transactions.Count - 1];
                return new ChainHead { Seq = last.Seq, Hash = last.Hash, Time = last.Timestamp };
            }

            return Committed.Head;
        }

        public ValueTask CreateAccountAsync(Account account, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(account);
            EnsureOpen();
            EnsureLocked(account.Id);
            if (_accounts.ContainsKey(account.Id) || FindAccount(Committed, account.Id) is not null)
            {
                throw new LedgerException(LedgerError.AccountExists, account.Id);
            }

            _accounts[account.Id] = account;
            _created.Add(account.Id);
            _balances[account.Id] = default;
            return ValueTask.CompletedTask;
        }

        public ValueTask UpdateAccountAsync(Account account, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(account);
            EnsureOpen();
            EnsureLocked(account.Id);
            if (!_accounts.ContainsKey(account.Id) && FindAccount(Committed, account.Id) is null)
            {
                throw new LedgerException(LedgerError.AccountNotFound, account.Id);
            }

            _accounts[account.Id] = account;
            return ValueTask.CompletedTask;
        }

        public ValueTask PutBalancesAsync(string accountId, Balances balances, CancellationToken cancellationToken)
        {
            EnsureOpen();
            EnsureLocked(accountId);
            _balances[accountId] = balances;
            return ValueTask.CompletedTask;
        }

        public ValueTask AppendTransactionAsync(Transaction transaction, IReadOnlyList<Entry> entries, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(entries);
            EnsureOpen();
            if (!_chainHeld)
            {
                throw new InvalidOperationException("AppendTransactionAsync requires the chain head; call GetChainHeadAsync first.");
            }

            foreach (Entry e in entries)
            {
                EnsureLocked(e.Account);
            }

            _transactions.Add(transaction);
            _entries.Add(entries.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask PutPendingAsync(PendingTransfer pending, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(pending);
            EnsureOpen();
            _pending[pending.Id] = pending;
            return ValueTask.CompletedTask;
        }

        /// <summary>Publishes the buffered writes atomically, after checking the unique constraints.</summary>
        public void Commit()
        {
            EnsureOpen();
            lock (_store._publishGate)
            {
                Snapshot s = _store._state;
                long next = s.Head.Seq + 1;
                foreach (Transaction t in _transactions)
                {
                    if (t.IdempotencyKey is { Length: > 0 } key && s.ByKey.ContainsKey(key))
                    {
                        throw new LedgerException(LedgerError.DuplicateIdempotencyKey, "\"" + key + "\"");
                    }

                    if (s.ById.ContainsKey(t.Id))
                    {
                        throw new InvalidOperationException("Duplicate transaction ID " + t.Id + ".");
                    }

                    if (t.Seq != next)
                    {
                        throw new InvalidOperationException("Append out of sequence: the chain expects the next transaction.");
                    }

                    next++;
                }

                foreach (string id in _created)
                {
                    if (s.Accounts.ContainsKey(id))
                    {
                        throw new LedgerException(LedgerError.AccountExists, id);
                    }
                }

                ImmutableDictionary<string, Account>.Builder accounts = s.Accounts.ToBuilder();
                foreach (KeyValuePair<string, Account> kv in _accounts)
                {
                    accounts[kv.Key] = kv.Value;
                }

                ImmutableDictionary<string, Balances>.Builder balances = s.Balances.ToBuilder();
                foreach (KeyValuePair<string, Balances> kv in _balances)
                {
                    balances[kv.Key] = kv.Value;
                }

                ImmutableDictionary<string, PendingTransfer>.Builder pending = s.Pending.ToBuilder();
                foreach (KeyValuePair<string, PendingTransfer> kv in _pending)
                {
                    pending[kv.Key] = kv.Value;
                }

                ImmutableList<Transaction>.Builder transactions = s.Transactions.ToBuilder();
                ImmutableDictionary<string, long>.Builder byId = s.ById.ToBuilder();
                ImmutableDictionary<string, long>.Builder byKey = s.ByKey.ToBuilder();
                ImmutableDictionary<string, string>.Builder reversals = s.Reversals.ToBuilder();
                ImmutableDictionary<string, ImmutableList<Entry>>.Builder entries = s.Entries.ToBuilder();
                ChainHead head = s.Head;
                for (int i = 0; i < _transactions.Count; i++)
                {
                    Transaction t = _transactions[i];
                    transactions.Add(t);
                    byId[t.Id] = t.Seq;
                    if (t.IdempotencyKey is { Length: > 0 } key)
                    {
                        byKey[key] = t.Seq;
                    }

                    if (t.Kind == TransactionKind.Reversal && t.Ref is not null)
                    {
                        reversals[t.Ref] = t.Id;
                    }

                    foreach (Entry e in _entries[i])
                    {
                        if (!entries.TryGetValue(e.Account, out ImmutableList<Entry>? existing) || existing is null)
                        {
                            existing = ImmutableList<Entry>.Empty;
                        }

                        entries[e.Account] = existing.Add(e);
                    }

                    head = new ChainHead { Seq = t.Seq, Hash = t.Hash, Time = t.Timestamp };
                }

                _store._state = new Snapshot(
                    accounts.ToImmutable(),
                    balances.ToImmutable(),
                    entries.ToImmutable(),
                    transactions.ToImmutable(),
                    byId.ToImmutable(),
                    byKey.ToImmutable(),
                    pending.ToImmutable(),
                    reversals.ToImmutable(),
                    head);
            }
        }

        /// <summary>Ends the update: releases the chain head if this update took it.</summary>
        public void Close()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            if (_chainHeld)
            {
                _chainHeld = false;
                _store._chainLock.Release();
            }
        }

        private void EnsureOpen()
        {
            if (_closed)
            {
                throw new ObjectDisposedException(nameof(IStoreTransaction), "The update has ended.");
            }
        }

        private void EnsureLocked(string accountId)
        {
            if (!_locked.Contains(accountId))
            {
                throw new LedgerException(LedgerError.NotLocked, accountId);
            }
        }
    }

    private static Account? FindAccount(Snapshot s, string id) => s.Accounts.TryGetValue(id, out Account? a) ? a : null;

    private static Balances? FindBalances(Snapshot s, string id) => s.Balances.TryGetValue(id, out Balances b) ? b : null;

    private static Transaction? FindTransaction(Snapshot s, string id) =>
        s.ById.TryGetValue(id, out long seq) ? s.Transactions[(int)(seq - 1)] : null;

    private static Transaction? FindByKey(Snapshot s, string key) =>
        !string.IsNullOrEmpty(key) && s.ByKey.TryGetValue(key, out long seq) ? s.Transactions[(int)(seq - 1)] : null;

    private static PendingTransfer? FindPending(Snapshot s, string id) => s.Pending.TryGetValue(id, out PendingTransfer? p) ? p : null;

    private static string? FindReversal(Snapshot s, string id) => s.Reversals.TryGetValue(id, out string? by) ? by : null;

    private static IReadOnlyList<Account> SortedAccounts(IEnumerable<Account> accounts)
    {
        Account[] sorted = accounts.ToArray();
        Array.Sort(sorted, static (x, y) => Utf8Order.CompareStrings(x.Id, y.Id));
        return sorted;
    }

    private static IReadOnlyList<T> Range<T>(ImmutableList<T> list, long after, int limit)
    {
        long n = list.Count;
        if (after < 0 || after >= n || limit <= 0)
        {
            return Array.Empty<T>();
        }

        long end = Math.Min(after + limit, n);
        return list.GetRange((int)after, (int)(end - after));
    }

    private static IReadOnlyList<Entry> EntriesOf(Snapshot s, string accountId, long after, int limit) =>
        s.Entries.TryGetValue(accountId, out ImmutableList<Entry>? list) && list is not null ? Range(list, after, limit) : Array.Empty<Entry>();

    private static Entry? EntryAt(Snapshot s, string accountId, DateTimeOffset at)
    {
        if (!s.Entries.TryGetValue(accountId, out ImmutableList<Entry>? list) || list is null)
        {
            return null;
        }

        // First index whose time is after `at`; entry times never decrease.
        int lo = 0;
        int hi = list.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) / 2);
            if (list[mid].Time > at)
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return lo == 0 ? null : list[lo - 1];
    }

    private static IReadOnlyList<PendingTransfer> ExpiredPending(Snapshot s, DateTimeOffset now, int limit)
    {
        var due = new List<PendingTransfer>();
        foreach (PendingTransfer p in s.Pending.Values)
        {
            if (p.Status == PendingStatus.Open && p.ExpiresAt is { } expires && expires <= now)
            {
                due.Add(p);
            }
        }

        due.Sort(static (x, y) =>
        {
            int byTime = x.ExpiresAt!.Value.CompareTo(y.ExpiresAt!.Value);
            return byTime != 0 ? byTime : Utf8Order.CompareStrings(x.Id, y.Id);
        });
        if (due.Count > limit)
        {
            due.RemoveRange(limit, due.Count - limit);
        }

        return due;
    }
}
