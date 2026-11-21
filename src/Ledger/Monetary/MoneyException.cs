namespace Ledger.Monetary;

/// <summary>The reason a monetary operation failed.</summary>
public enum MoneyError
{
    /// <summary>The result does not fit in 64-bit minor units.</summary>
    Overflow = 1,

    /// <summary>The operands have different currencies.</summary>
    CurrencyMismatch = 2,

    /// <summary>The currency code is not in the ISO 4217 table.</summary>
    UnknownCurrency = 3,

    /// <summary>The text is not a decimal amount.</summary>
    Syntax = 4,

    /// <summary>The text has more decimal places than the currency's exponent allows, e.g. <c>"1.234"</c> for BRL.</summary>
    Precision = 5,

    /// <summary>Allocation ratios are empty, negative, sum to zero or overflow.</summary>
    Ratios = 6,
}

/// <summary>Thrown when a monetary operation cannot produce an exact result.</summary>
public sealed class MoneyException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="code">Why the operation failed.</param>
    /// <param name="detail">Optional context appended to the message.</param>
    /// <param name="innerException">The exception that caused this one, if any.</param>
    public MoneyException(MoneyError code, string? detail = null, Exception? innerException = null)
        : base(BuildMessage(code, detail), innerException)
    {
        Code = code;
    }

    /// <summary>Why the operation failed.</summary>
    public MoneyError Code { get; }

    internal static string Describe(MoneyError code) => code switch
    {
        MoneyError.Overflow => "money: overflow",
        MoneyError.CurrencyMismatch => "money: currency mismatch",
        MoneyError.UnknownCurrency => "money: unknown currency",
        MoneyError.Syntax => "money: invalid amount syntax",
        MoneyError.Precision => "money: too many decimal places",
        MoneyError.Ratios => "money: invalid allocation ratios",
        _ => "money: error",
    };

    private static string BuildMessage(MoneyError code, string? detail) =>
        string.IsNullOrEmpty(detail) ? Describe(code) : Describe(code) + ": " + detail;
}
