using System.Globalization;

namespace Ledger;

/// <summary>The side of a posting or of an account's normal balance. The zero value is invalid so that a forgotten field is caught.</summary>
public enum Direction : byte
{
    /// <summary>Debit: increases assets and expenses.</summary>
    Debit = 1,

    /// <summary>Credit: increases liabilities, equity and revenue.</summary>
    Credit = 2,
}

/// <summary>The accounting classification of an account.</summary>
public enum AccountType : byte
{
    /// <summary>Debit-normal: cash, reserves, receivables.</summary>
    Asset = 1,

    /// <summary>Credit-normal: customer wallets, payables.</summary>
    Liability = 2,

    /// <summary>Credit-normal: capital.</summary>
    Equity = 3,

    /// <summary>Credit-normal: fees earned.</summary>
    Revenue = 4,

    /// <summary>Debit-normal: costs incurred.</summary>
    Expense = 5,
}

/// <summary>Flags that modify how the ledger treats an account.</summary>
[Flags]
public enum AccountFlags
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>Rejects any transaction that would make the available balance (posted minus outgoing holds, on the normal side) negative. Customer wallets are the typical case.</summary>
    NoOverdraft = 1,

    /// <summary>Rejects posted and held effects in both directions, e.g. under a judicial block. Releasing a hold is still allowed.</summary>
    Frozen = 2,
}

/// <summary>What a transaction does and how its postings are interpreted.</summary>
public enum TransactionKind : byte
{
    /// <summary>Postings move posted balances immediately.</summary>
    Standard = 1,

    /// <summary>Postings are holds: they move pending balances and reduce available balances until resolved.</summary>
    Pending = 2,

    /// <summary>Settles all or part of a pending transfer. Its postings are the amounts posted; the whole hold is released.</summary>
    PostPending = 3,

    /// <summary>Cancels a pending transfer. Its postings are the released holds.</summary>
    VoidPending = 4,

    /// <summary>A void performed by the expiry sweep after the pending transfer's timeout.</summary>
    ExpirePending = 5,

    /// <summary>Compensates a standard or posted-pending transaction with mirrored postings.</summary>
    Reversal = 6,
}

/// <summary>Which balance counters an <see cref="Entry"/> moved.</summary>
public enum EntryPhase : byte
{
    /// <summary>Moves posted counters.</summary>
    Posted = 1,

    /// <summary>Adds to pending counters.</summary>
    Held = 2,

    /// <summary>Removes a hold from pending counters.</summary>
    Released = 3,
}

/// <summary>The state of a two-phase transfer. <see cref="PendingStatus.Open"/> is the only non-terminal state.</summary>
public enum PendingStatus : byte
{
    /// <summary>Held and awaiting resolution.</summary>
    Open = 1,

    /// <summary>Settled, fully or partially.</summary>
    Posted = 2,

    /// <summary>Cancelled.</summary>
    Voided = 3,

    /// <summary>Voided by the expiry sweep.</summary>
    Expired = 4,
}

/// <summary>Helpers for the ledger enumerations. The <c>ToCode</c> names are the stable, lower-case names used by the Go implementation.</summary>
public static class LedgerEnumExtensions
{
    /// <summary>Returns <c>"debit"</c> or <c>"credit"</c>.</summary>
    /// <param name="direction">The direction.</param>
    /// <returns>The name.</returns>
    public static string ToCode(this Direction direction) => direction switch
    {
        Direction.Debit => "debit",
        Direction.Credit => "credit",
        _ => "Direction(" + Number((int)direction) + ")",
    };

    /// <summary>Returns the other direction.</summary>
    /// <param name="direction">The direction.</param>
    /// <returns>Credit for debit, debit otherwise.</returns>
    public static Direction Opposite(this Direction direction) => direction == Direction.Debit ? Direction.Credit : Direction.Debit;

    /// <summary>Returns the lower-case type name, e.g. <c>"liability"</c>.</summary>
    /// <param name="type">The account type.</param>
    /// <returns>The name.</returns>
    public static string ToCode(this AccountType type) => type switch
    {
        AccountType.Asset => "asset",
        AccountType.Liability => "liability",
        AccountType.Equity => "equity",
        AccountType.Revenue => "revenue",
        AccountType.Expense => "expense",
        _ => "AccountType(" + Number((int)type) + ")",
    };

    /// <summary>Returns the side on which balances of this type grow: debit for assets and expenses, credit otherwise.</summary>
    /// <param name="type">The account type.</param>
    /// <returns>The normal balance side.</returns>
    public static Direction NormalBalance(this AccountType type) =>
        type == AccountType.Asset || type == AccountType.Expense ? Direction.Debit : Direction.Credit;

    /// <summary>Lists the set flags, e.g. <c>"no_overdraft|frozen"</c>, or <c>"none"</c>.</summary>
    /// <param name="flags">The flags.</param>
    /// <returns>The names joined by <c>|</c>.</returns>
    public static string ToCode(this AccountFlags flags)
    {
        var parts = new List<string>(3);
        if ((flags & AccountFlags.NoOverdraft) != 0)
        {
            parts.Add("no_overdraft");
        }

        if ((flags & AccountFlags.Frozen) != 0)
        {
            parts.Add("frozen");
        }

        int rest = (int)(flags & ~(AccountFlags.NoOverdraft | AccountFlags.Frozen));
        if (rest != 0)
        {
            parts.Add("0x" + rest.ToString("x", CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? "none" : string.Join("|", parts);
    }

    /// <summary>Returns the kind name, e.g. <c>"post_pending"</c>.</summary>
    /// <param name="kind">The transaction kind.</param>
    /// <returns>The name.</returns>
    public static string ToCode(this TransactionKind kind) => kind switch
    {
        TransactionKind.Standard => "standard",
        TransactionKind.Pending => "pending",
        TransactionKind.PostPending => "post_pending",
        TransactionKind.VoidPending => "void_pending",
        TransactionKind.ExpirePending => "expire_pending",
        TransactionKind.Reversal => "reversal",
        _ => "Kind(" + Number((int)kind) + ")",
    };

    /// <summary>Returns <c>"posted"</c>, <c>"held"</c> or <c>"released"</c>.</summary>
    /// <param name="phase">The entry phase.</param>
    /// <returns>The name.</returns>
    public static string ToCode(this EntryPhase phase) => phase switch
    {
        EntryPhase.Posted => "posted",
        EntryPhase.Held => "held",
        EntryPhase.Released => "released",
        _ => "Phase(" + Number((int)phase) + ")",
    };

    /// <summary>Returns the status name, e.g. <c>"open"</c>.</summary>
    /// <param name="status">The pending status.</param>
    /// <returns>The name.</returns>
    public static string ToCode(this PendingStatus status) => status switch
    {
        PendingStatus.Open => "open",
        PendingStatus.Posted => "posted",
        PendingStatus.Voided => "voided",
        PendingStatus.Expired => "expired",
        _ => "PendingStatus(" + Number((int)status) + ")",
    };

    internal static bool IsDefined(this Direction direction) => direction == Direction.Debit || direction == Direction.Credit;

    internal static bool IsDefined(this AccountType type) => type >= AccountType.Asset && type <= AccountType.Expense;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
