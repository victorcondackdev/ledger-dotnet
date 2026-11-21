using Ledger.Monetary;

namespace Ledger;

/// <summary>One leg of a transaction: a positive amount debited or credited to an account. The direction carries the sign.</summary>
public readonly record struct Posting
{
    /// <summary>Creates a posting.</summary>
    /// <param name="account">The account identifier.</param>
    /// <param name="direction">Debit or credit.</param>
    /// <param name="amount">A positive amount in the account's currency.</param>
    public Posting(string account, Direction direction, Money amount)
    {
        Account = account;
        Direction = direction;
        Amount = amount;
    }

    /// <summary>The account identifier.</summary>
    public string Account { get; init; }

    /// <summary>Debit or credit.</summary>
    public Direction Direction { get; init; }

    /// <summary>A positive amount in the account's currency.</summary>
    public Money Amount { get; init; }

    /// <summary>Creates a debit posting.</summary>
    /// <param name="account">The account identifier.</param>
    /// <param name="amount">A positive amount.</param>
    /// <returns>The posting.</returns>
    public static Posting Debit(string account, Money amount) => new(account, Direction.Debit, amount);

    /// <summary>Creates a credit posting.</summary>
    /// <param name="account">The account identifier.</param>
    /// <param name="amount">A positive amount.</param>
    /// <returns>The posting.</returns>
    public static Posting Credit(string account, Money amount) => new(account, Direction.Credit, amount);

    /// <summary>Returns e.g. <c>"debit wallet:alice BRL 10.00"</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => this.Direction.ToCode() + " " + Account + " " + Amount.ToString();
}

/// <summary>An immutable, sealed ledger record.</summary>
public sealed record Transaction
{
    /// <summary>The unique identifier assigned by the ledger (UUIDv7 by default).</summary>
    public required string Id { get; init; }

    /// <summary>The 1-based position in the audit chain. It is gap-free and gives every consumer (statements, outbox, replicas) a total order.</summary>
    public long Seq { get; init; }

    /// <summary>How the postings are interpreted.</summary>
    public TransactionKind Kind { get; init; }

    /// <summary>The caller's idempotency key, or null.</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>The related transaction: the pending transfer for post, void and expire, the original for a reversal; otherwise null.</summary>
    public string? Ref { get; init; }

    /// <summary>Assigned by the ledger clock; never decreases along the chain.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>The timeout of a <see cref="TransactionKind.Pending"/> transaction, or null if it never expires.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Free text for statements.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Free key/value data (end-to-end IDs, case numbers), part of the sealed record.</summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = Canonical.EmptyMetadata;

    /// <summary>The legs, in request order.</summary>
    public required IReadOnlyList<Posting> Postings { get; init; }

    /// <summary>The fingerprint of the request, compared on idempotent replays.</summary>
    public Sha256Hash RequestHash { get; init; }

    /// <summary>The <see cref="Hash"/> of the transaction with <c>Seq - 1</c> (zero for the first).</summary>
    public Sha256Hash PrevHash { get; init; }

    /// <summary>SHA-256 of <see cref="PrevHash"/> followed by the canonical encoding of every other field.</summary>
    public Sha256Hash Hash { get; init; }
}

/// <summary>
/// The effect of one transaction leg on one account, with the account's
/// counters right after it. Statements and point-in-time balances are built
/// from entries; in SQL they are the ledger_entries table.
/// </summary>
public sealed record Entry
{
    /// <summary>The 1-based position among the account's entries. A transaction with two legs on the same account produces two entries with the same <see cref="Seq"/> and distinct <see cref="AccountSeq"/>. Cursors use it.</summary>
    public long AccountSeq { get; init; }

    /// <summary>The transaction's chain sequence number.</summary>
    public long Seq { get; init; }

    /// <summary>The transaction identifier.</summary>
    public required string TransactionId { get; init; }

    /// <summary>The transaction kind.</summary>
    public TransactionKind Kind { get; init; }

    /// <summary>The transaction timestamp.</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>The account.</summary>
    public required string Account { get; init; }

    /// <summary>The side of the leg.</summary>
    public Direction Direction { get; init; }

    /// <summary>Which counters moved.</summary>
    public EntryPhase Phase { get; init; }

    /// <summary>The positive amount, in minor units of the account's currency.</summary>
    public long Amount { get; init; }

    /// <summary>The account's counters after this entry.</summary>
    public Balances After { get; init; }
}

/// <summary>The state-machine record of a <see cref="TransactionKind.Pending"/> transaction. The transaction itself is immutable; this record moves from open to exactly one terminal state.</summary>
public sealed record PendingTransfer
{
    /// <summary>The pending transaction's identifier.</summary>
    public required string Id { get; init; }

    /// <summary>The current state.</summary>
    public PendingStatus Status { get; init; }

    /// <summary>When the hold expires, or null for never.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The held legs.</summary>
    public required IReadOnlyList<Posting> Postings { get; init; }

    /// <summary>The post, void or expire transaction, once resolved.</summary>
    public string? ResolvedBy { get; init; }
}

/// <summary>The tip of the audit chain.</summary>
public readonly record struct ChainHead
{
    /// <summary>The sequence number of the last transaction (0 for an empty ledger).</summary>
    public long Seq { get; init; }

    /// <summary>The hash of the last transaction. Publish or notarize it to anchor the history.</summary>
    public Sha256Hash Hash { get; init; }

    /// <summary>The timestamp of the last transaction.</summary>
    public DateTimeOffset Time { get; init; }
}
