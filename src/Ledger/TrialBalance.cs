using System.Globalization;
using Ledger.Monetary;

namespace Ledger;

/// <summary>One account's posted and pending totals.</summary>
public sealed record TrialBalanceRow
{
    /// <summary>The account.</summary>
    public required Account Account { get; init; }

    /// <summary>Sum of posted debits.</summary>
    public Money DebitsPosted { get; init; }

    /// <summary>Sum of posted credits.</summary>
    public Money CreditsPosted { get; init; }

    /// <summary>Sum of open debit holds.</summary>
    public Money DebitsPending { get; init; }

    /// <summary>Sum of open credit holds.</summary>
    public Money CreditsPending { get; init; }

    /// <summary>The posted balance on the account's normal side.</summary>
    public Money Balance { get; init; }
}

/// <summary>A trial balance summed for one currency, in minor units.</summary>
public readonly record struct CurrencyTotals
{
    /// <summary>Sum of posted debits.</summary>
    public long DebitsPosted { get; init; }

    /// <summary>Sum of posted credits.</summary>
    public long CreditsPosted { get; init; }

    /// <summary>Sum of open debit holds.</summary>
    public long DebitsPending { get; init; }

    /// <summary>Sum of open credit holds.</summary>
    public long CreditsPending { get; init; }
}

/// <summary>
/// Every account's totals from one consistent snapshot. In a correct ledger
/// debits equal credits per currency, both posted and pending, which makes
/// the trial balance the cheapest global invariant check there is.
/// </summary>
public sealed class TrialBalance
{
    /// <summary>Creates a trial balance from rows and per-currency totals.</summary>
    /// <param name="rows">One row per account, in account order.</param>
    /// <param name="totals">Totals per currency.</param>
    public TrialBalance(IReadOnlyList<TrialBalanceRow> rows, IReadOnlyDictionary<Currency, CurrencyTotals> totals)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(totals);
        Rows = rows;
        Totals = totals;
    }

    /// <summary>One row per account, in account order.</summary>
    public IReadOnlyList<TrialBalanceRow> Rows { get; }

    /// <summary>Totals per currency.</summary>
    public IReadOnlyDictionary<Currency, CurrencyTotals> Totals { get; }

    /// <summary>Whether debits equal credits in every currency, posted and pending.</summary>
    public bool IsBalanced => FindImbalance() is null;

    /// <summary>The currencies present, sorted by code.</summary>
    /// <returns>The currencies.</returns>
    public IReadOnlyList<Currency> Currencies()
    {
        Currency[] currencies = Totals.Keys.ToArray();
        Array.Sort(currencies);
        return currencies;
    }

    /// <summary>Throws unless debits equal credits in every currency, posted and pending.</summary>
    /// <exception cref="LedgerException">The books do not balance (<see cref="LedgerError.Unbalanced"/>).</exception>
    public void Check()
    {
        string? imbalance = FindImbalance();
        if (imbalance is not null)
        {
            throw new LedgerException(LedgerError.Unbalanced, imbalance);
        }
    }

    private string? FindImbalance()
    {
        foreach (Currency c in Currencies())
        {
            CurrencyTotals t = Totals[c];
            if (t.DebitsPosted != t.CreditsPosted)
            {
                return c.Code + " posted debits " + N(t.DebitsPosted) + " != credits " + N(t.CreditsPosted);
            }

            if (t.DebitsPending != t.CreditsPending)
            {
                return c.Code + " pending debits " + N(t.DebitsPending) + " != credits " + N(t.CreditsPending);
            }
        }

        return null;
    }

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);
}
