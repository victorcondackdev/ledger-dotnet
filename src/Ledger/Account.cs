using Ledger.Monetary;

namespace Ledger;

/// <summary>A ledger account. Accounts are never deleted; their currency, type and normal balance never change.</summary>
public sealed record Account
{
    /// <summary>The unique, caller-chosen identifier, e.g. <c>"wallet:alice"</c>. Identifiers are ordered byte-wise on their UTF-8 encoding, which is also the lock order.</summary>
    public required string Id { get; init; }

    /// <summary>An optional chart-of-accounts code, e.g. <c>"2.1.01"</c>.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>An optional human-readable label.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The only currency the account accepts.</summary>
    public Currency Currency { get; init; }

    /// <summary>The accounting classification.</summary>
    public AccountType Type { get; init; }

    /// <summary>The side on which the balance grows. Left at its default it is derived from <c>Type</c>; set it explicitly for contra accounts.</summary>
    public Direction Normal { get; init; }

    /// <summary>Flags such as <see cref="AccountFlags.NoOverdraft"/> and <see cref="AccountFlags.Frozen"/>.</summary>
    public AccountFlags Flags { get; init; }

    /// <summary>Set by the ledger when the account is opened.</summary>
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// The four raw counters of an account, in minor units of its currency, in
/// the model popularized by TigerBeetle. Posted counters only grow; pending
/// counters shrink when a hold is released.
/// </summary>
public readonly record struct Balances
{
    /// <summary>Sum of posted debits.</summary>
    public long DebitsPosted { get; init; }

    /// <summary>Sum of posted credits.</summary>
    public long CreditsPosted { get; init; }

    /// <summary>Sum of open debit holds.</summary>
    public long DebitsPending { get; init; }

    /// <summary>Sum of open credit holds.</summary>
    public long CreditsPending { get; init; }

    /// <summary>The number of entries recorded for the account; the latest entry has <c>AccountSeq == Version</c>. In SQL it doubles as an optimistic-lock column.</summary>
    public long Version { get; init; }

    internal bool TryPosted(Direction normal, out long value) => normal == Direction.Debit
        ? CheckedMath.TrySubtract(DebitsPosted, CreditsPosted, out value)
        : CheckedMath.TrySubtract(CreditsPosted, DebitsPosted, out value);

    internal bool TryPending(Direction normal, out long value) => normal == Direction.Debit
        ? CheckedMath.TrySubtract(DebitsPending, CreditsPending, out value)
        : CheckedMath.TrySubtract(CreditsPending, DebitsPending, out value);

    internal bool TryAvailable(Direction normal, out long value)
    {
        if (!TryPosted(normal, out long posted))
        {
            value = 0;
            return false;
        }

        // Incoming holds are not spendable; outgoing holds are.
        long outgoing = normal == Direction.Credit ? DebitsPending : CreditsPending;
        return CheckedMath.TrySubtract(posted, outgoing, out value);
    }
}

/// <summary>An account's balance interpreted on its normal side.</summary>
public sealed record Balance
{
    /// <summary>The account.</summary>
    public required string Account { get; init; }

    /// <summary>The settled balance: debits minus credits for debit-normal accounts, credits minus debits otherwise.</summary>
    public Money Posted { get; init; }

    /// <summary>The net amount held by open pending transfers, positive when incoming.</summary>
    public Money Pending { get; init; }

    /// <summary>Posted minus outgoing holds: what can be spent now.</summary>
    public Money Available { get; init; }

    /// <summary>The underlying counters.</summary>
    public Balances Raw { get; init; }

    internal static Balance Of(Account account, Balances raw)
    {
        _ = raw.TryPosted(account.Normal, out long posted);
        _ = raw.TryPending(account.Normal, out long pending);
        _ = raw.TryAvailable(account.Normal, out long available);
        return new Balance
        {
            Account = account.Id,
            Posted = new Money(posted, account.Currency),
            Pending = new Money(pending, account.Currency),
            Available = new Money(available, account.Currency),
            Raw = raw,
        };
    }
}
