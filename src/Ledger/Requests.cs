using Ledger.Monetary;

namespace Ledger;

/// <summary>Describes a new transaction for <c>PostAsync</c> or <c>HoldAsync</c>.</summary>
public sealed record PostRequest
{
    /// <summary>Makes the request safe to retry: the first commit wins and later identical requests return it. Null or empty disables idempotency. Keys share one namespace across all operations.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Free text for statements.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Free key/value data, part of the sealed record.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>The legs; debits must equal credits per currency.</summary>
    public required IReadOnlyList<Posting> Postings { get; init; }

    /// <summary>For holds only: the hold expires this long after its timestamp. Zero means it never expires.</summary>
    public TimeSpan Timeout { get; init; }
}

/// <summary>Resolves a pending transfer (<c>PostPendingAsync</c>, <c>VoidPendingAsync</c>) or reverses a transaction (<c>ReverseAsync</c>).</summary>
public sealed record ResolveRequest
{
    /// <summary>The pending transfer to resolve, or the transaction to reverse.</summary>
    public required string Id { get; init; }

    /// <summary>As in <see cref="PostRequest.IdempotencyKey"/>.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>Free text for statements.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Free key/value data, part of the sealed record.</summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }

    /// <summary>
    /// For <c>PostPendingAsync</c> only: posts part of the hold; null posts
    /// all of it. The hold must be in a single currency and the amount must be
    /// positive and not exceed it. Legs are scaled with <c>Money.Allocate</c>,
    /// so a multi-leg hold (amount plus fee) keeps its split; the unposted
    /// remainder is released.
    /// </summary>
    public Money? Amount { get; init; }
}

/// <summary>Selects a page of an account statement.</summary>
public sealed record StatementOptions
{
    /// <summary>The <see cref="Statement.NextCursor"/> of the previous page; null starts at the beginning.</summary>
    public string? Cursor { get; init; }

    /// <summary>The page size: default 50, maximum 1000.</summary>
    public int Limit { get; init; }

    /// <summary>Adds held and released entries (pending activity); by default only posted movements are listed.</summary>
    public bool IncludeHolds { get; init; }
}

/// <summary>One movement on an account.</summary>
public sealed record StatementLine
{
    /// <summary>The transaction's chain sequence number.</summary>
    public long Seq { get; init; }

    /// <summary>The transaction identifier.</summary>
    public required string TransactionId { get; init; }

    /// <summary>The transaction kind.</summary>
    public TransactionKind Kind { get; init; }

    /// <summary>The transaction timestamp.</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>The transaction description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The side of the leg.</summary>
    public Direction Direction { get; init; }

    /// <summary>Which counters moved.</summary>
    public EntryPhase Phase { get; init; }

    /// <summary>Signed on the account's normal side: positive when the movement increases the balance (or, for holds, the pending amount).</summary>
    public Money Amount { get; init; }

    /// <summary>The posted balance after the movement.</summary>
    public Money Balance { get; init; }

    /// <summary>The available balance after the movement.</summary>
    public Money Available { get; init; }
}

/// <summary>A page of account movements, oldest first.</summary>
public sealed record Statement
{
    /// <summary>The account.</summary>
    public required Account Account { get; init; }

    /// <summary>The movements on this page.</summary>
    public required IReadOnlyList<StatementLine> Lines { get; init; }

    /// <summary>Fetches the next page; null when this is the last one. Cursors are opaque and stable: new activity never shifts pages already returned.</summary>
    public string? NextCursor { get; init; }
}

/// <summary>Options for <see cref="GeneralLedger"/>.</summary>
public sealed class LedgerOptions
{
    /// <summary>The clock for timestamps and expiry (default: the system clock, UTC). Tests and simulations inject a manual clock to make runs reproducible.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>The transaction identifier generator (default: UUIDv7, time-ordered, which keeps SQL primary-key indexes append-mostly). Identifiers must be unique.</summary>
    public Func<string> IdGenerator { get; init; } = UuidV7.NewString;

    /// <summary>
    /// Hooks called with every newly committed transaction, after the store
    /// commit and after all locks are released, on the committing thread.
    /// They are not called for idempotent replays, run at most once per
    /// transaction and not at all if the process dies right after the commit:
    /// use them to wake an outbox relay, not as the outbox itself. A durable
    /// relay reads <c>GetTransactionsAsync</c> from its last processed
    /// sequence number, which is gap-free.
    /// </summary>
    public IReadOnlyList<Action<Transaction>> OnCommit { get; init; } = Array.Empty<Action<Transaction>>();
}
