using System.Buffers.Binary;
using System.Globalization;
using Ledger.Monetary;

namespace Ledger;

public sealed partial class GeneralLedger
{
    private const int VerifyPage = 512;
    private const int ReplayPage = 1024;
    private static readonly byte[] CursorPrefix = { (byte)'s', (byte)'1' };

    /// <summary>Returns an account.</summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The account.</returns>
    /// <exception cref="LedgerException"><see cref="LedgerError.AccountNotFound"/>.</exception>
    public ValueTask<Account> GetAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        return _store.ViewAsync<Account>(
            async (r, ct) => await r.FindAccountAsync(accountId, ct).ConfigureAwait(false)
                ?? throw new LedgerException(LedgerError.AccountNotFound, accountId),
            cancellationToken);
    }

    /// <summary>Returns all accounts in ascending identifier order.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The accounts.</returns>
    public ValueTask<IReadOnlyList<Account>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
        _store.ViewAsync<IReadOnlyList<Account>>((r, ct) => r.GetAccountsAsync(ct), cancellationToken);

    /// <summary>Returns a transaction.</summary>
    /// <param name="transactionId">The transaction identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The transaction.</returns>
    /// <exception cref="LedgerException"><see cref="LedgerError.TransactionNotFound"/>.</exception>
    public ValueTask<Transaction> GetTransactionAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transactionId);
        return _store.ViewAsync<Transaction>(
            async (r, ct) => await r.FindTransactionAsync(transactionId, ct).ConfigureAwait(false)
                ?? throw new LedgerException(LedgerError.TransactionNotFound, transactionId),
            cancellationToken);
    }

    /// <summary>Returns up to <paramref name="limit"/> transactions with a sequence number greater than <paramref name="afterSeq"/>, in chain order. An outbox relay or replica tails the ledger with it.</summary>
    /// <param name="afterSeq">The last sequence number already processed (0 for the start).</param>
    /// <param name="limit">The page size.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The transactions.</returns>
    public ValueTask<IReadOnlyList<Transaction>> GetTransactionsAsync(long afterSeq, int limit, CancellationToken cancellationToken = default) =>
        _store.ViewAsync<IReadOnlyList<Transaction>>((r, ct) => r.GetTransactionsAsync(afterSeq, limit, ct), cancellationToken);

    /// <summary>Returns the state of a pending transfer.</summary>
    /// <param name="transactionId">The pending transaction identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The pending transfer.</returns>
    /// <exception cref="LedgerException"><see cref="LedgerError.TransactionNotFound"/>.</exception>
    public ValueTask<PendingTransfer> GetPendingAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transactionId);
        return _store.ViewAsync<PendingTransfer>(
            async (r, ct) => await r.FindPendingAsync(transactionId, ct).ConfigureAwait(false)
                ?? throw new LedgerException(LedgerError.TransactionNotFound, transactionId),
            cancellationToken);
    }

    /// <summary>Returns an account's current balance.</summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The balance.</returns>
    /// <exception cref="LedgerException"><see cref="LedgerError.AccountNotFound"/>.</exception>
    public ValueTask<Balance> GetBalanceAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        return _store.ViewAsync<Balance>(
            async (r, ct) =>
            {
                Account account = await r.FindAccountAsync(accountId, ct).ConfigureAwait(false)
                    ?? throw new LedgerException(LedgerError.AccountNotFound, accountId);
                Balances raw = await r.FindBalancesAsync(accountId, ct).ConfigureAwait(false)
                    ?? throw new LedgerException(LedgerError.AccountNotFound, accountId);
                return Balance.Of(account, raw);
            },
            cancellationToken);
    }

    /// <summary>
    /// Returns an account's balance as of <paramref name="at"/>, inclusive: the
    /// state after the last entry timestamped at or before it. Because
    /// timestamps never decrease along the chain this is a single indexed
    /// lookup, not a replay.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="at">The point in time.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The balance.</returns>
    /// <exception cref="LedgerException"><see cref="LedgerError.AccountNotFound"/>.</exception>
    public ValueTask<Balance> GetBalanceAtAsync(string accountId, DateTimeOffset at, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        return _store.ViewAsync<Balance>(
            async (r, ct) =>
            {
                Account account = await r.FindAccountAsync(accountId, ct).ConfigureAwait(false)
                    ?? throw new LedgerException(LedgerError.AccountNotFound, accountId);
                Entry? entry = await r.FindEntryAtAsync(accountId, at, ct).ConfigureAwait(false);
                return Balance.Of(account, entry?.After ?? default);
            },
            cancellationToken);
    }

    /// <summary>Returns a page of an account's movements with running balances. Cursors are opaque and stable: new activity never shifts pages already returned.</summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="options">The page to fetch; null for the first page with the defaults.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The page.</returns>
    /// <exception cref="LedgerException"><see cref="LedgerError.AccountNotFound"/> or <see cref="LedgerError.InvalidCursor"/>.</exception>
    public ValueTask<Statement> GetStatementAsync(string accountId, StatementOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        options ??= new StatementOptions();
        long start = DecodeCursor(options.Cursor);
        int limit = options.Limit <= 0 ? 50 : Math.Min(options.Limit, 1000);
        bool includeHolds = options.IncludeHolds;
        return _store.ViewAsync<Statement>(
            async (r, ct) =>
            {
                Account account = await r.FindAccountAsync(accountId, ct).ConfigureAwait(false)
                    ?? throw new LedgerException(LedgerError.AccountNotFound, accountId);
                var lines = new List<StatementLine>();
                var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
                long after = start;
                long last = 0;
                while (lines.Count <= limit)
                {
                    IReadOnlyList<Entry> entries = await r.GetEntriesAsync(accountId, after, limit + 1, ct).ConfigureAwait(false);
                    foreach (Entry e in entries)
                    {
                        after = e.AccountSeq;
                        if (e.Phase != EntryPhase.Posted && !includeHolds)
                        {
                            continue;
                        }

                        if (lines.Count == limit)
                        {
                            return new Statement { Account = account, Lines = lines, NextCursor = EncodeCursor(last) };
                        }

                        if (!descriptions.TryGetValue(e.TransactionId, out string? description))
                        {
                            Transaction t = await r.FindTransactionAsync(e.TransactionId, ct).ConfigureAwait(false)
                                ?? throw new LedgerException(LedgerError.TransactionNotFound, e.TransactionId);
                            description = t.Description;
                            descriptions[e.TransactionId] = description;
                        }

                        lines.Add(Line(account, e, description));
                        last = e.AccountSeq;
                    }

                    if (entries.Count <= limit)
                    {
                        break;
                    }
                }

                return new Statement { Account = account, Lines = lines };
            },
            cancellationToken);
    }

    /// <summary>Computes the trial balance of all accounts from one consistent snapshot.</summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The trial balance; call <see cref="TrialBalance.Check"/> to assert it.</returns>
    /// <exception cref="LedgerException"><see cref="LedgerError.Overflow"/> if a total does not fit in 64 bits.</exception>
    public ValueTask<TrialBalance> GetTrialBalanceAsync(CancellationToken cancellationToken = default) =>
        _store.ViewAsync<TrialBalance>(
            async (r, ct) =>
            {
                IReadOnlyList<Account> accounts = await r.GetAccountsAsync(ct).ConfigureAwait(false);
                var rows = new List<TrialBalanceRow>(accounts.Count);
                var totals = new Dictionary<Currency, CurrencyTotals>();
                foreach (Account a in accounts)
                {
                    Balances raw = await r.FindBalancesAsync(a.Id, ct).ConfigureAwait(false)
                        ?? throw new LedgerException(LedgerError.AccountNotFound, a.Id);
                    Currency c = a.Currency;
                    rows.Add(new TrialBalanceRow
                    {
                        Account = a,
                        DebitsPosted = new Money(raw.DebitsPosted, c),
                        CreditsPosted = new Money(raw.CreditsPosted, c),
                        DebitsPending = new Money(raw.DebitsPending, c),
                        CreditsPending = new Money(raw.CreditsPending, c),
                        Balance = Balance.Of(a, raw).Posted,
                    });
                    _ = totals.TryGetValue(c, out CurrencyTotals t);
                    bool ok = CheckedMath.TryAdd(t.DebitsPosted, raw.DebitsPosted, out long dp);
                    ok &= CheckedMath.TryAdd(t.CreditsPosted, raw.CreditsPosted, out long cp);
                    ok &= CheckedMath.TryAdd(t.DebitsPending, raw.DebitsPending, out long dq);
                    ok &= CheckedMath.TryAdd(t.CreditsPending, raw.CreditsPending, out long cq);
                    if (!ok)
                    {
                        throw new LedgerException(LedgerError.Overflow, c.Code + " trial balance totals");
                    }

                    totals[c] = new CurrencyTotals { DebitsPosted = dp, CreditsPosted = cp, DebitsPending = dq, CreditsPending = cq };
                }

                return new TrialBalance(rows, totals);
            },
            cancellationToken);

    /// <summary>
    /// Walks the whole audit chain and recomputes every hash. Any edit,
    /// insertion, deletion or reordering of recorded transactions is detected.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The verified head: publish or notarize its hash to anchor the history.</returns>
    /// <exception cref="ChainBrokenException">The first transaction whose sequence, link, timestamp or content does not match.</exception>
    public ValueTask<ChainHead> VerifyChainAsync(CancellationToken cancellationToken = default) =>
        _store.ViewAsync<ChainHead>(
            async (r, ct) =>
            {
                var head = default(ChainHead);
                while (true)
                {
                    IReadOnlyList<Transaction> page = await r.GetTransactionsAsync(head.Seq, VerifyPage, ct).ConfigureAwait(false);
                    foreach (Transaction t in page)
                    {
                        if (t.Seq != head.Seq + 1)
                        {
                            throw new ChainBrokenException(t.Seq, t.Id, "sequence gap: expected " + (head.Seq + 1).ToString(CultureInfo.InvariantCulture));
                        }

                        if (t.PrevHash != head.Hash)
                        {
                            throw new ChainBrokenException(t.Seq, t.Id, "previous hash does not match");
                        }

                        if (t.Timestamp < head.Time)
                        {
                            throw new ChainBrokenException(t.Seq, t.Id, "timestamp moves backwards");
                        }

                        if (Canonical.Seal(t) != t.Hash)
                        {
                            throw new ChainBrokenException(t.Seq, t.Id, "content hash does not match");
                        }

                        head = new ChainHead { Seq = t.Seq, Hash = t.Hash, Time = t.Timestamp };
                    }

                    if (page.Count < VerifyPage)
                    {
                        return head;
                    }
                }
            },
            cancellationToken);

    /// <summary>
    /// Replays every account's entries from zero and checks that each entry's
    /// recorded counters and the stored balances agree with the replay.
    /// Together with <c>VerifyChainAsync</c> and <see cref="TrialBalance.Check"/>
    /// it audits the whole ledger.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the audit passes.</returns>
    /// <exception cref="LedgerException"><see cref="LedgerError.BalanceMismatch"/>.</exception>
    public async ValueTask VerifyBalancesAsync(CancellationToken cancellationToken = default)
    {
        await _store.ViewAsync<bool>(
            async (r, ct) =>
            {
                IReadOnlyList<Account> accounts = await r.GetAccountsAsync(ct).ConfigureAwait(false);
                foreach (Account a in accounts)
                {
                    var replayed = default(Balances);
                    long after = 0;
                    while (true)
                    {
                        IReadOnlyList<Entry> entries = await r.GetEntriesAsync(a.Id, after, ReplayPage, ct).ConfigureAwait(false);
                        foreach (Entry e in entries)
                        {
                            string where = a.Id + " entry " + e.AccountSeq.ToString(CultureInfo.InvariantCulture);
                            LedgerError error = TryApplyEffect(replayed, e.Direction, e.Phase, e.Amount, out Balances next);
                            if (error != 0)
                            {
                                throw new LedgerException(LedgerError.BalanceMismatch, where + ": " + LedgerException.Describe(error));
                            }

                            replayed = next with { Version = next.Version + 1 };
                            if (e.AccountSeq != replayed.Version || e.After != replayed)
                            {
                                throw new LedgerException(LedgerError.BalanceMismatch, where);
                            }

                            after = e.AccountSeq;
                        }

                        if (entries.Count < ReplayPage)
                        {
                            break;
                        }
                    }

                    Balances stored = await r.FindBalancesAsync(a.Id, ct).ConfigureAwait(false)
                        ?? throw new LedgerException(LedgerError.AccountNotFound, a.Id);
                    if (stored != replayed)
                    {
                        throw new LedgerException(LedgerError.BalanceMismatch, a.Id + " stored " + stored.ToString() + ", replayed " + replayed.ToString());
                    }
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static StatementLine Line(Account account, Entry e, string description)
    {
        long amount = e.Amount;
        if (e.Direction != account.Normal)
        {
            amount = -amount;
        }

        if (e.Phase == EntryPhase.Released)
        {
            amount = -amount;
        }

        Balance b = Balance.Of(account, e.After);
        return new StatementLine
        {
            Seq = e.Seq,
            TransactionId = e.TransactionId,
            Kind = e.Kind,
            Time = e.Time,
            Description = description,
            Direction = e.Direction,
            Phase = e.Phase,
            Amount = new Money(amount, account.Currency),
            Balance = b.Posted,
            Available = b.Available,
        };
    }

    private static string EncodeCursor(long accountSeq)
    {
        Span<byte> b = stackalloc byte[10];
        CursorPrefix.CopyTo(b);
        BinaryPrimitives.WriteUInt64BigEndian(b.Slice(2), unchecked((ulong)accountSeq));
        return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static long DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return 0;
        }

        foreach (char c in cursor)
        {
            bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
            if (!ok)
            {
                throw new LedgerException(LedgerError.InvalidCursor);
            }
        }

        if (cursor.Length % 4 == 1)
        {
            throw new LedgerException(LedgerError.InvalidCursor);
        }

        string base64 = cursor.Replace('-', '+').Replace('_', '/') + new string('=', (4 - (cursor.Length % 4)) % 4);
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException e)
        {
            throw new LedgerException(LedgerError.InvalidCursor, null, e);
        }

        if (bytes.Length != 10 || bytes[0] != CursorPrefix[0] || bytes[1] != CursorPrefix[1])
        {
            throw new LedgerException(LedgerError.InvalidCursor);
        }

        return unchecked((long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(2)));
    }
}
