using System.Collections.ObjectModel;
using System.Globalization;
using Ledger.Internal;
using Ledger.Monetary;

namespace Ledger;

/// <summary>
/// Enforces double-entry invariants on top of an <see cref="ILedgerStore"/>.
/// Every write is a single store update, so each of the following holds
/// atomically or the write is rejected with no effect: debits equal credits
/// per currency; every posting's currency equals its account's; frozen
/// accounts receive no postings; no-overdraft accounts never have a negative
/// available balance; an idempotency key maps to exactly one transaction;
/// history is append-only; and every transaction is hash-chained to its
/// predecessor. Safe for concurrent use.
/// </summary>
public sealed partial class GeneralLedger
{
    /// <summary>The maximum number of legs in one transaction. It bounds the lock set and the work done while holding it.</summary>
    public const int MaxPostings = 128;

    private const int ExpireBatch = 256;

    private readonly ILedgerStore _store;
    private readonly TimeProvider _time;
    private readonly Func<string> _newId;
    private readonly Action<Transaction>[] _onCommit;

    /// <summary>Creates a ledger backed by <paramref name="store"/>.</summary>
    /// <param name="store">The store.</param>
    /// <param name="options">Clock, identifier generator and commit hooks; null for the defaults.</param>
    public GeneralLedger(ILedgerStore store, LedgerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        options ??= new LedgerOptions();
        _store = store;
        _time = options.TimeProvider ?? TimeProvider.System;
        _newId = options.IdGenerator ?? new Func<string>(UuidV7.NewString);
        _onCommit = (options.OnCommit ?? Array.Empty<Action<Transaction>>()).ToArray();
    }

    /// <summary>Creates an account with zero balances.</summary>
    /// <param name="account">The definition; <see cref="Account.Normal"/> defaults from the type.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The account with its normal balance and creation time filled in.</returns>
    /// <exception cref="LedgerException"><see cref="LedgerError.InvalidAccount"/> or <see cref="LedgerError.AccountExists"/>.</exception>
    public async ValueTask<Account> OpenAccountAsync(Account account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        Account a = Normalize(account) with { CreatedAt = Now() };
        await _store.UpdateAsync(
            new[] { a.Id },
            async (tx, ct) =>
            {
                await tx.CreateAccountAsync(a, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
        return a;
    }

    /// <summary>Sets the frozen flag: the account accepts no postings until unfrozen. Open holds on it can still be voided or expire.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the flag is stored.</returns>
    public ValueTask FreezeAsync(string accountId, CancellationToken cancellationToken = default) =>
        SetFlagAsync(accountId, AccountFlags.Frozen, true, cancellationToken);

    /// <summary>Clears the frozen flag.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>A task that completes when the flag is stored.</returns>
    public ValueTask UnfreezeAsync(string accountId, CancellationToken cancellationToken = default) =>
        SetFlagAsync(accountId, AccountFlags.Frozen, false, cancellationToken);

    /// <summary>Commits a transaction that moves posted balances immediately: a Pix transfer, a fee charge, a deposit.</summary>
    /// <param name="request">The request; <see cref="PostRequest.Timeout"/> must be zero.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The sealed transaction, or the original one for an idempotent replay.</returns>
    /// <exception cref="LedgerException">The request violates an invariant; nothing was written.</exception>
    public ValueTask<Transaction> PostAsync(PostRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Posting[] postings = ValidatePostings(request.Postings);
        if (request.Timeout != TimeSpan.Zero)
        {
            throw new LedgerException(LedgerError.InvalidPostings, "timeout is only valid for holds");
        }

        var plan = new Plan
        {
            Kind = TransactionKind.Standard,
            IdempotencyKey = NormalizeKey(request.IdempotencyKey),
            RequestHash = Canonical.Fingerprint("post", request),
            Description = request.Description ?? string.Empty,
            Metadata = CopyMetadata(request.Metadata),
            Lock = LockSet(postings),
            Postings = postings,
            Effects = EffectsOf(postings, EntryPhase.Posted),
        };
        return CommitAsync(plan, cancellationToken);
    }

    /// <summary>
    /// Commits the first phase of a two-phase transfer: the postings are
    /// recorded as pending, reducing the debited accounts' available balance
    /// without settling. Resolve it with <c>PostPendingAsync</c> or
    /// <c>VoidPendingAsync</c>; with a timeout, <c>ExpirePendingAsync</c>
    /// voids it once due. Card authorizations and Pix funds blocked under MED
    /// work this way.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The sealed pending transaction, or the original one for an idempotent replay.</returns>
    /// <exception cref="LedgerException">The request violates an invariant; nothing was written.</exception>
    public ValueTask<Transaction> HoldAsync(PostRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Posting[] postings = ValidatePostings(request.Postings);
        if (request.Timeout < TimeSpan.Zero)
        {
            throw new LedgerException(LedgerError.InvalidPostings, "negative timeout");
        }

        if (request.Timeout.Ticks > long.MaxValue / 100)
        {
            throw new LedgerException(LedgerError.InvalidPostings, "timeout too large");
        }

        var plan = new Plan
        {
            Kind = TransactionKind.Pending,
            IdempotencyKey = NormalizeKey(request.IdempotencyKey),
            RequestHash = Canonical.Fingerprint("hold", request),
            Description = request.Description ?? string.Empty,
            Metadata = CopyMetadata(request.Metadata),
            Lock = LockSet(postings),
            Postings = postings,
            Effects = EffectsOf(postings, EntryPhase.Held),
            Timeout = request.Timeout,
            After = (tx, t, ct) => tx.PutPendingAsync(
                new PendingTransfer { Id = t.Id, Status = PendingStatus.Open, ExpiresAt = t.ExpiresAt, Postings = t.Postings },
                ct),
        };
        return CommitAsync(plan, cancellationToken);
    }

    /// <summary>Commits the second phase of a two-phase transfer, posting all or part of an open hold and releasing the rest.</summary>
    /// <param name="request">The pending transfer and, optionally, the amount to post.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The sealed post-pending transaction, or the original one for an idempotent replay.</returns>
    /// <exception cref="LedgerException">The hold is not open, expired, or the amount is invalid; nothing was written.</exception>
    public ValueTask<Transaction> PostPendingAsync(ResolveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ResolveAsync(request, TransactionKind.PostPending, PendingStatus.Posted, cancellationToken);
    }

    /// <summary>Cancels an open hold, releasing it entirely.</summary>
    /// <param name="request">The pending transfer; <see cref="ResolveRequest.Amount"/> must be null.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The sealed void-pending transaction, or the original one for an idempotent replay.</returns>
    /// <exception cref="LedgerException">The hold is not open; nothing was written.</exception>
    public ValueTask<Transaction> VoidPendingAsync(ResolveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount is not null)
        {
            throw new LedgerException(LedgerError.InvalidPostings, "amount is only valid for PostPending");
        }

        return ResolveAsync(request, TransactionKind.VoidPending, PendingStatus.Voided, cancellationToken);
    }

    /// <summary>
    /// Voids every open hold whose timeout has passed, each in its own
    /// transaction of kind <see cref="TransactionKind.ExpirePending"/>. Run it
    /// periodically; until then an expired hold still counts against the
    /// available balance but can no longer be posted.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>How many holds were expired.</returns>
    public async ValueTask<int> ExpirePendingAsync(CancellationToken cancellationToken = default)
    {
        int n = 0;
        while (true)
        {
            DateTimeOffset now = Now();
            IReadOnlyList<PendingTransfer> due = await _store.ViewAsync<IReadOnlyList<PendingTransfer>>(
                (r, ct) => r.GetExpiredPendingAsync(now, ExpireBatch, ct),
                cancellationToken).ConfigureAwait(false);
            bool progressed = false;
            foreach (PendingTransfer pt in due)
            {
                try
                {
                    await ResolveAsync(
                        new ResolveRequest { Id = pt.Id, Description = "expired" },
                        TransactionKind.ExpirePending,
                        PendingStatus.Expired,
                        cancellationToken).ConfigureAwait(false);
                    n++;
                    progressed = true;
                }
                catch (LedgerException e) when (e.Code == LedgerError.NotPending)
                {
                    // Resolved concurrently; nothing to do.
                }
            }

            if (due.Count < ExpireBatch || !progressed)
            {
                return n;
            }
        }
    }

    /// <summary>
    /// Commits a compensating transaction that mirrors every posting of a
    /// standard or posted-pending transaction, linked to it by
    /// <see cref="Transaction.Ref"/>. History is never edited, and a
    /// transaction can be reversed once. The reversal is subject to the same
    /// invariants as any transaction: it fails with
    /// <see cref="LedgerError.InsufficientFunds"/> if the funds already left a
    /// no-overdraft account.
    /// </summary>
    /// <param name="request">The transaction to reverse; <see cref="ResolveRequest.Amount"/> must be null.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The sealed reversal, or the original one for an idempotent replay.</returns>
    /// <exception cref="LedgerException">The transaction cannot be reversed; nothing was written.</exception>
    public async ValueTask<Transaction> ReverseAsync(ResolveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount is not null)
        {
            throw new LedgerException(LedgerError.InvalidPostings, "partial reversals are not supported");
        }

        Transaction original = await GetTransactionAsync(request.Id, cancellationToken).ConfigureAwait(false);
        if (original.Kind != TransactionKind.Standard && original.Kind != TransactionKind.PostPending)
        {
            throw new LedgerException(LedgerError.NotReversible, request.Id + " is " + original.Kind.ToCode());
        }

        var mirrored = new Posting[original.Postings.Count];
        for (int i = 0; i < mirrored.Length; i++)
        {
            Posting p = original.Postings[i];
            mirrored[i] = p with { Direction = p.Direction.Opposite() };
        }

        string id = request.Id;
        var plan = new Plan
        {
            Kind = TransactionKind.Reversal,
            IdempotencyKey = NormalizeKey(request.IdempotencyKey),
            RequestHash = Canonical.Fingerprint("reverse", request),
            Ref = id,
            Description = request.Description ?? string.Empty,
            Metadata = CopyMetadata(request.Metadata),
            Lock = LockSet(mirrored),
            Postings = mirrored,
            Effects = EffectsOf(mirrored, EntryPhase.Posted),
            Build = async (tx, ct) =>
            {
                string? by = await tx.FindReversalAsync(id, ct).ConfigureAwait(false);
                if (by is not null)
                {
                    throw new LedgerException(LedgerError.AlreadyReversed, id + " by " + by);
                }
            },
        };
        return await CommitAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    private DateTimeOffset Now() => _time.GetUtcNow().ToUniversalTime();

    private async ValueTask SetFlagAsync(string accountId, AccountFlags flag, bool on, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountId);
        await _store.UpdateAsync(
            new[] { accountId },
            async (tx, ct) =>
            {
                Account a = await tx.FindAccountAsync(accountId, ct).ConfigureAwait(false)
                    ?? throw new LedgerException(LedgerError.AccountNotFound, accountId);
                a = a with { Flags = on ? a.Flags | flag : a.Flags & ~flag };
                await tx.UpdateAccountAsync(a, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Transaction> ResolveAsync(ResolveRequest r, TransactionKind kind, PendingStatus status, CancellationToken cancellationToken)
    {
        PendingTransfer pt = await GetPendingAsync(r.Id, cancellationToken).ConfigureAwait(false);
        var plan = new Plan
        {
            Kind = kind,
            IdempotencyKey = NormalizeKey(r.IdempotencyKey),
            RequestHash = Canonical.Fingerprint(kind.ToCode(), r),
            Ref = r.Id,
            Description = r.Description ?? string.Empty,
            Metadata = CopyMetadata(r.Metadata),
            Lock = LockSet(pt.Postings),
        };
        plan.Build = async (tx, ct) =>
        {
            // Re-read under the account locks: the snapshot above only served
            // to compute the lock set, which never changes.
            pt = await tx.FindPendingAsync(r.Id, ct).ConfigureAwait(false)
                ?? throw new LedgerException(LedgerError.TransactionNotFound, r.Id);
            if (pt.Status != PendingStatus.Open)
            {
                throw new LedgerException(LedgerError.NotPending, r.Id + " is " + pt.Status.ToCode());
            }

            DateTimeOffset now = Now();
            if (kind == TransactionKind.ExpirePending && (pt.ExpiresAt is null || now < pt.ExpiresAt.Value))
            {
                throw new LedgerException(LedgerError.NotPending, r.Id + " is not due");
            }

            if (kind == TransactionKind.PostPending && pt.ExpiresAt is { } expires && now >= expires)
            {
                throw new LedgerException(LedgerError.PendingExpired, r.Id + " at " + expires.ToString("O", CultureInfo.InvariantCulture));
            }

            var effects = EffectsOf(pt.Postings, EntryPhase.Released);
            if (kind == TransactionKind.PostPending)
            {
                Posting[] posted = ScalePostings(pt.Postings, r.Amount);
                plan.Postings = posted;
                effects.AddRange(EffectsOf(posted, EntryPhase.Posted));
            }
            else
            {
                plan.Postings = pt.Postings.ToArray();
            }

            plan.Effects = effects;
        };
        plan.After = (tx, t, ct) => tx.PutPendingAsync(pt with { Status = status, ResolvedBy = t.Id }, ct);
        return await CommitAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs a plan in one store update: idempotency check, state checks, balance updates with invariant checks, sealing and append.</summary>
    private async ValueTask<Transaction> CommitAsync(Plan plan, CancellationToken cancellationToken)
    {
        string id = _newId();
        for (int attempt = 0; ; attempt++)
        {
            Transaction? result = null;
            bool replay = false;
            try
            {
                await _store.UpdateAsync(
                    plan.Lock,
                    async (tx, ct) =>
                    {
                        result = null;
                        replay = false;
                        if (plan.IdempotencyKey is not null)
                        {
                            Transaction? previous = await tx.FindTransactionByIdempotencyKeyAsync(plan.IdempotencyKey, ct).ConfigureAwait(false);
                            if (previous is not null)
                            {
                                if (previous.RequestHash != plan.RequestHash)
                                {
                                    throw new LedgerException(
                                        LedgerError.IdempotencyConflict,
                                        "key \"" + plan.IdempotencyKey + "\" is tx " + previous.Id);
                                }

                                result = previous;
                                replay = true;
                                return false;
                            }
                        }

                        if (plan.Build is not null)
                        {
                            await plan.Build(tx, ct).ConfigureAwait(false);
                        }

                        result = await ApplyAsync(tx, plan, id, ct).ConfigureAwait(false);
                        return true;
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (LedgerException e) when (e.Code == LedgerError.DuplicateIdempotencyKey && attempt == 0)
            {
                // Lost a race on the key at commit: the retry replays the winner.
                continue;
            }

            Transaction committed = result ?? throw new InvalidOperationException("The store did not run the update.");
            if (!replay)
            {
                foreach (Action<Transaction> hook in _onCommit)
                {
                    hook(committed);
                }
            }

            return committed;
        }
    }

    /// <summary>Validates effects against current state, seals the transaction and writes it. Runs inside the store update with all accounts locked.</summary>
    private async ValueTask<Transaction> ApplyAsync(IStoreTransaction tx, Plan plan, string id, CancellationToken ct)
    {
        var states = new Dictionary<string, AccountState>(plan.Lock.Length, StringComparer.Ordinal);
        foreach (string accountId in plan.Lock)
        {
            Account account = await tx.FindAccountAsync(accountId, ct).ConfigureAwait(false)
                ?? throw new LedgerException(LedgerError.AccountNotFound, accountId);
            Balances balances = await tx.FindBalancesAsync(accountId, ct).ConfigureAwait(false)
                ?? throw new LedgerException(LedgerError.AccountNotFound, accountId);
            states[accountId] = new AccountState(account, balances);
        }

        var pendingEntries = new List<(Effect Effect, Balances After)>(plan.Effects.Count);
        foreach (Effect e in plan.Effects)
        {
            AccountState st = states[e.Account];
            if (e.Amount.Currency != st.Account.Currency)
            {
                throw new LedgerException(
                    LedgerError.CurrencyMismatch,
                    e.Amount.Currency.Code + " posting to " + st.Account.Currency.Code + " account " + e.Account);
            }

            if ((st.Account.Flags & AccountFlags.Frozen) != 0 && e.Phase != EntryPhase.Released)
            {
                throw new LedgerException(LedgerError.AccountFrozen, e.Account);
            }

            LedgerError error = TryApplyEffect(st.Balances, e.Direction, e.Phase, e.Amount.MinorUnits, out Balances next);
            if (error != 0)
            {
                throw new LedgerException(error, "account " + e.Account);
            }

            st.Balances = next with { Version = next.Version + 1 };
            st.Touched = true;
            pendingEntries.Add((e, st.Balances));
        }

        foreach (string accountId in plan.Lock)
        {
            AccountState st = states[accountId];
            if ((st.Account.Flags & AccountFlags.NoOverdraft) == 0)
            {
                continue;
            }

            if (!st.Balances.TryAvailable(st.Account.Normal, out long available))
            {
                throw new LedgerException(LedgerError.Overflow, "account " + accountId);
            }

            if (available < 0)
            {
                throw new LedgerException(
                    LedgerError.InsufficientFunds,
                    "account " + accountId + " would have " + new Money(available, st.Account.Currency).ToString() + " available");
            }
        }

        // Everything is validated; take the chain head as late as possible.
        ChainHead head = await tx.GetChainHeadAsync(ct).ConfigureAwait(false);
        DateTimeOffset ts = Now();
        if (ts < head.Time)
        {
            ts = head.Time;
        }

        DateTimeOffset? expiresAt = null;
        if (plan.Timeout > TimeSpan.Zero)
        {
            if (plan.Timeout > DateTimeOffset.MaxValue - ts)
            {
                throw new LedgerException(LedgerError.InvalidPostings, "timeout too large");
            }

            expiresAt = ts + plan.Timeout;
        }

        var sealing = new Transaction
        {
            Id = id,
            Seq = head.Seq + 1,
            Kind = plan.Kind,
            IdempotencyKey = plan.IdempotencyKey,
            Ref = plan.Ref,
            Timestamp = ts,
            ExpiresAt = expiresAt,
            Description = plan.Description,
            Metadata = plan.Metadata,
            Postings = Array.AsReadOnly(plan.Postings.ToArray()),
            RequestHash = plan.RequestHash,
            PrevHash = head.Hash,
        };
        Transaction t = sealing with { Hash = Canonical.Seal(sealing) };

        var entries = new Entry[pendingEntries.Count];
        for (int i = 0; i < entries.Length; i++)
        {
            (Effect e, Balances after) = pendingEntries[i];
            entries[i] = new Entry
            {
                AccountSeq = after.Version,
                Seq = t.Seq,
                TransactionId = id,
                Kind = plan.Kind,
                Time = ts,
                Account = e.Account,
                Direction = e.Direction,
                Phase = e.Phase,
                Amount = e.Amount.MinorUnits,
                After = after,
            };
        }

        foreach (string accountId in plan.Lock)
        {
            AccountState st = states[accountId];
            if (st.Touched)
            {
                await tx.PutBalancesAsync(accountId, st.Balances, ct).ConfigureAwait(false);
            }
        }

        await tx.AppendTransactionAsync(t, entries, ct).ConfigureAwait(false);
        if (plan.After is not null)
        {
            await plan.After(tx, t, ct).ConfigureAwait(false);
        }

        return t;
    }

    /// <summary>Applies one effect to a copy of the counters; returns 0 on success or the reason it cannot be applied.</summary>
    internal static LedgerError TryApplyEffect(Balances b, Direction direction, EntryPhase phase, long amount, out Balances result)
    {
        result = b;
        if (phase == EntryPhase.Released)
        {
            long current = direction == Direction.Debit ? b.DebitsPending : b.CreditsPending;
            if (current < amount)
            {
                return LedgerError.ExceedsPending;
            }

            result = direction == Direction.Debit
                ? b with { DebitsPending = current - amount }
                : b with { CreditsPending = current - amount };
            return 0;
        }

        long counter = (phase, direction) switch
        {
            (EntryPhase.Posted, Direction.Debit) => b.DebitsPosted,
            (EntryPhase.Posted, _) => b.CreditsPosted,
            (_, Direction.Debit) => b.DebitsPending,
            _ => b.CreditsPending,
        };
        if (!CheckedMath.TryAdd(counter, amount, out long sum))
        {
            return LedgerError.Overflow;
        }

        result = (phase, direction) switch
        {
            (EntryPhase.Posted, Direction.Debit) => b with { DebitsPosted = sum },
            (EntryPhase.Posted, _) => b with { CreditsPosted = sum },
            (_, Direction.Debit) => b with { DebitsPending = sum },
            _ => b with { CreditsPending = sum },
        };
        return 0;
    }

    private static Account Normalize(Account a)
    {
        if (string.IsNullOrEmpty(a.Id))
        {
            throw new LedgerException(LedgerError.InvalidAccount, "empty ID");
        }

        if (!a.Currency.IsValid)
        {
            throw new LedgerException(LedgerError.InvalidAccount, "unknown currency \"" + a.Currency.Code + "\"");
        }

        if (!a.Type.IsDefined())
        {
            throw new LedgerException(LedgerError.InvalidAccount, "unknown type " + ((int)a.Type).ToString(CultureInfo.InvariantCulture));
        }

        Account normalized = a.Normal == 0 ? a with { Normal = a.Type.NormalBalance() } : a;
        if (!normalized.Normal.IsDefined())
        {
            throw new LedgerException(LedgerError.InvalidAccount, "unknown normal balance " + ((int)a.Normal).ToString(CultureInfo.InvariantCulture));
        }

        return normalized;
    }

    private static string? NormalizeKey(string? key) => string.IsNullOrEmpty(key) ? null : key;

    private static IReadOnlyDictionary<string, string> CopyMetadata(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return Canonical.EmptyMetadata;
        }

        var copy = new Dictionary<string, string>(metadata.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> kv in metadata)
        {
            copy[kv.Key] = kv.Value ?? string.Empty;
        }

        return new ReadOnlyDictionary<string, string>(copy);
    }

    /// <summary>Checks the static shape of a request and returns a private copy of its postings.</summary>
    private static Posting[] ValidatePostings(IReadOnlyList<Posting>? postings)
    {
        Posting[] ps = postings?.ToArray() ?? Array.Empty<Posting>();
        if (ps.Length < 2)
        {
            throw new LedgerException(LedgerError.InvalidPostings, "need at least two postings");
        }

        if (ps.Length > MaxPostings)
        {
            throw new LedgerException(LedgerError.InvalidPostings, "more than " + MaxPostings.ToString(CultureInfo.InvariantCulture) + " postings");
        }

        var totals = new Dictionary<Currency, long[]>();
        for (int i = 0; i < ps.Length; i++)
        {
            Posting p = ps[i];
            string where = "posting " + i.ToString(CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(p.Account))
            {
                throw new LedgerException(LedgerError.InvalidPostings, where + " has no account");
            }

            if (!p.Direction.IsDefined())
            {
                throw new LedgerException(LedgerError.InvalidPostings, where + " has no direction");
            }

            if (!p.Amount.Currency.IsValid)
            {
                throw new LedgerException(LedgerError.InvalidPostings, where + " has unknown currency \"" + p.Amount.Currency.Code + "\"");
            }

            if (!p.Amount.IsPositive)
            {
                throw new LedgerException(LedgerError.InvalidPostings, where + " amount " + p.Amount.ToString() + " is not positive");
            }

            if (!totals.TryGetValue(p.Amount.Currency, out long[]? sums))
            {
                sums = new long[2];
                totals[p.Amount.Currency] = sums;
            }

            int side = p.Direction == Direction.Debit ? 0 : 1;
            if (!CheckedMath.TryAdd(sums[side], p.Amount.MinorUnits, out sums[side]))
            {
                throw new LedgerException(LedgerError.Overflow, "posting totals");
            }
        }

        foreach (KeyValuePair<Currency, long[]> kv in totals)
        {
            if (kv.Value[0] != kv.Value[1])
            {
                throw new LedgerException(
                    LedgerError.Unbalanced,
                    kv.Key.Code + " debits " + new Money(kv.Value[0], kv.Key).ToDecimalString()
                        + ", credits " + new Money(kv.Value[1], kv.Key).ToDecimalString());
            }
        }

        return ps;
    }

    /// <summary>Returns the distinct accounts of the postings in ascending order: the global lock order that makes concurrent updates deadlock-free.</summary>
    private static string[] LockSet(IEnumerable<Posting> postings)
    {
        var ids = new SortedSet<string>(Utf8Order.Comparer);
        foreach (Posting p in postings)
        {
            ids.Add(p.Account);
        }

        return ids.ToArray();
    }

    private static List<Effect> EffectsOf(IReadOnlyList<Posting> postings, EntryPhase phase)
    {
        var effects = new List<Effect>(postings.Count);
        foreach (Posting p in postings)
        {
            effects.Add(new Effect(p.Account, p.Direction, phase, p.Amount));
        }

        return effects;
    }

    /// <summary>Scales a hold's postings to <paramref name="amount"/> (null: all of it), splitting each side across its legs with <c>Money.Allocate</c>. Legs scaled to zero are dropped.</summary>
    private static Posting[] ScalePostings(IReadOnlyList<Posting> ps, Money? amount)
    {
        if (amount is not { } a)
        {
            return ps.ToArray();
        }

        Currency currency = ps[0].Amount.Currency;
        long total = 0;
        foreach (Posting p in ps)
        {
            if (p.Amount.Currency != currency)
            {
                throw new LedgerException(LedgerError.ExceedsPending, "partial post of a multi-currency hold");
            }

            if (p.Direction == Direction.Debit)
            {
                total += p.Amount.MinorUnits; // bounded: validated at hold time
            }
        }

        if (a.Currency != currency)
        {
            throw new LedgerException(LedgerError.ExceedsPending, a.Currency.Code + " amount for a " + currency.Code + " hold");
        }

        if (!a.IsPositive)
        {
            throw new LedgerException(LedgerError.InvalidPostings, "amount " + a.ToString() + " is not positive");
        }

        if (a.MinorUnits > total)
        {
            throw new LedgerException(
                LedgerError.ExceedsPending,
                a.ToDecimalString() + " > " + new Money(total, currency).ToDecimalString());
        }

        if (a.MinorUnits == total)
        {
            return ps.ToArray();
        }

        var scaled = new Money[ps.Count];
        foreach (Direction side in new[] { Direction.Debit, Direction.Credit })
        {
            var indexes = new List<int>();
            var ratios = new List<long>();
            for (int i = 0; i < ps.Count; i++)
            {
                if (ps[i].Direction == side)
                {
                    indexes.Add(i);
                    ratios.Add(ps[i].Amount.MinorUnits);
                }
            }

            Money[] parts = a.Allocate(ratios.ToArray());
            for (int k = 0; k < indexes.Count; k++)
            {
                scaled[indexes[k]] = parts[k];
            }
        }

        var result = new List<Posting>(ps.Count);
        for (int i = 0; i < ps.Count; i++)
        {
            if (scaled[i].IsPositive)
            {
                result.Add(new Posting(ps[i].Account, ps[i].Direction, scaled[i]));
            }
        }

        return result.ToArray();
    }

    /// <summary>One change to one account's counters.</summary>
    private readonly record struct Effect(string Account, Direction Direction, EntryPhase Phase, Money Amount);

    private sealed class AccountState
    {
        public AccountState(Account account, Balances balances)
        {
            Account = account;
            Balances = balances;
        }

        public Account Account { get; }

        public Balances Balances { get; set; }

        public bool Touched { get; set; }
    }

    /// <summary>A transaction being prepared by <c>CommitAsync</c>.</summary>
    private sealed class Plan
    {
        public TransactionKind Kind { get; init; }

        public string? IdempotencyKey { get; init; }

        public Sha256Hash RequestHash { get; init; }

        public string? Ref { get; init; }

        public string Description { get; init; } = string.Empty;

        public IReadOnlyDictionary<string, string> Metadata { get; init; } = Canonical.EmptyMetadata;

        public TimeSpan Timeout { get; init; }

        public string[] Lock { get; init; } = Array.Empty<string>();

        public IReadOnlyList<Posting> Postings { get; set; } = Array.Empty<Posting>();

        public List<Effect> Effects { get; set; } = new();

        /// <summary>If set, runs first inside the store update to check state and fill postings and effects.</summary>
        public Func<IStoreTransaction, CancellationToken, ValueTask>? Build { get; set; }

        /// <summary>If set, runs after the transaction is appended.</summary>
        public Func<IStoreTransaction, Transaction, CancellationToken, ValueTask>? After { get; set; }
    }
}
