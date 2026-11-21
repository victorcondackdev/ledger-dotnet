using System.Globalization;

namespace Ledger;

/// <summary>The reason a ledger operation was rejected. A rejected write leaves no trace.</summary>
public enum LedgerError
{
    /// <summary>A referenced account does not exist.</summary>
    AccountNotFound = 1,

    /// <summary>An account with the same identifier already exists.</summary>
    AccountExists = 2,

    /// <summary>The account definition is incomplete or inconsistent (empty identifier, unknown currency, type or normal balance).</summary>
    InvalidAccount = 3,

    /// <summary>A referenced transaction or pending transfer does not exist.</summary>
    TransactionNotFound = 4,

    /// <summary>The postings are malformed: fewer than two, too many, a non-positive amount, an unknown direction or currency, or an option that does not apply to the operation.</summary>
    InvalidPostings = 5,

    /// <summary>Debits and credits differ in some currency, or a trial balance does not sum to zero.</summary>
    Unbalanced = 6,

    /// <summary>A posting's currency differs from its account's.</summary>
    CurrencyMismatch = 7,

    /// <summary>A posting targets a frozen account.</summary>
    AccountFrozen = 8,

    /// <summary>The transaction would leave a no-overdraft account with a negative available balance.</summary>
    InsufficientFunds = 9,

    /// <summary>A balance or total would no longer fit in 64 bits.</summary>
    Overflow = 10,

    /// <summary>An idempotency key was reused with a different request.</summary>
    IdempotencyConflict = 11,

    /// <summary>A store detected at commit that another writer recorded the same idempotency key first. The ledger handles it by replaying; callers do not see it unless the store misbehaves.</summary>
    DuplicateIdempotencyKey = 12,

    /// <summary>The transaction is not an open pending transfer (already resolved, or not due for expiry).</summary>
    NotPending = 13,

    /// <summary>The pending transfer expired and can no longer be posted.</summary>
    PendingExpired = 14,

    /// <summary>A partial post is larger than the held amount or in another currency, or a release exceeds the pending counter.</summary>
    ExceedsPending = 15,

    /// <summary>Only standard and posted-pending transactions can be reversed.</summary>
    NotReversible = 16,

    /// <summary>The transaction has already been reversed.</summary>
    AlreadyReversed = 17,

    /// <summary>Audit chain verification failed; see <see cref="ChainBrokenException"/>.</summary>
    ChainBroken = 18,

    /// <summary>A store was asked for an account that was not locked for the update. It indicates a bug.</summary>
    NotLocked = 19,

    /// <summary>Stored balances disagree with the replayed entry history.</summary>
    BalanceMismatch = 20,

    /// <summary>The pagination cursor is malformed.</summary>
    InvalidCursor = 21,
}

/// <summary>Thrown when the ledger rejects an operation. Test <see cref="Code"/> to react to specific failures.</summary>
public class LedgerException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="code">Why the operation was rejected.</param>
    /// <param name="detail">Optional context appended to the message.</param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    public LedgerException(LedgerError code, string? detail = null, Exception? innerException = null)
        : base(BuildMessage(code, detail), innerException)
    {
        Code = code;
    }

    /// <summary>Why the operation was rejected.</summary>
    public LedgerError Code { get; }

    internal static string Describe(LedgerError code) => code switch
    {
        LedgerError.AccountNotFound => "account not found",
        LedgerError.AccountExists => "account already exists",
        LedgerError.InvalidAccount => "invalid account",
        LedgerError.TransactionNotFound => "transaction not found",
        LedgerError.InvalidPostings => "invalid postings",
        LedgerError.Unbalanced => "debits and credits do not balance",
        LedgerError.CurrencyMismatch => "posting currency differs from account currency",
        LedgerError.AccountFrozen => "account is frozen",
        LedgerError.InsufficientFunds => "insufficient funds",
        LedgerError.Overflow => "balance overflow",
        LedgerError.IdempotencyConflict => "idempotency key reused with a different request",
        LedgerError.DuplicateIdempotencyKey => "duplicate idempotency key",
        LedgerError.NotPending => "transaction is not an open pending transfer",
        LedgerError.PendingExpired => "pending transfer expired",
        LedgerError.ExceedsPending => "amount exceeds pending amount",
        LedgerError.NotReversible => "transaction cannot be reversed",
        LedgerError.AlreadyReversed => "transaction already reversed",
        LedgerError.ChainBroken => "audit chain broken",
        LedgerError.NotLocked => "account not locked in this update",
        LedgerError.BalanceMismatch => "stored balance does not match history",
        LedgerError.InvalidCursor => "invalid cursor",
        _ => "ledger error " + ((int)code).ToString(CultureInfo.InvariantCulture),
    };

    private static string BuildMessage(LedgerError code, string? detail) =>
        string.IsNullOrEmpty(detail) ? Describe(code) : Describe(code) + ": " + detail;
}

/// <summary>Describes the first transaction at which audit chain verification found the chain inconsistent.</summary>
public sealed class ChainBrokenException : LedgerException
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="seq">The sequence number of the offending transaction.</param>
    /// <param name="transactionId">Its identifier.</param>
    /// <param name="reason">What did not match.</param>
    public ChainBrokenException(long seq, string transactionId, string reason)
        : base(LedgerError.ChainBroken, "at seq " + seq.ToString(CultureInfo.InvariantCulture) + " (tx " + transactionId + "): " + reason)
    {
        Seq = seq;
        TransactionId = transactionId;
        Reason = reason;
    }

    /// <summary>The sequence number of the offending transaction.</summary>
    public long Seq { get; }

    /// <summary>The offending transaction's identifier.</summary>
    public string TransactionId { get; }

    /// <summary>What did not match.</summary>
    public string Reason { get; }
}
