using Ledger.Internal;

namespace Ledger.Monetary;

/// <summary>
/// An ISO 4217 alphabetic currency code such as <c>BRL</c>. Only codes in the
/// built-in table are valid (see <see cref="IsValid"/>); any code can still be
/// represented, which keeps parsing of stored data total.
/// </summary>
public readonly struct Currency : IEquatable<Currency>, IComparable<Currency>
{
    private static readonly Dictionary<string, Info> Table = new(StringComparer.Ordinal)
    {
        ["ARS"] = new(2, 32, "ARS"),
        ["AUD"] = new(2, 36, "A$"),
        ["BHD"] = new(3, 48, "BD"),
        ["BRL"] = new(2, 986, "R$"),
        ["CAD"] = new(2, 124, "C$"),
        ["CHF"] = new(2, 756, "CHF"),
        ["CLF"] = new(4, 990, "UF"),
        ["CLP"] = new(0, 152, "CLP"),
        ["CNY"] = new(2, 156, "CN\u00A5"),
        ["COP"] = new(2, 170, "COP"),
        ["EUR"] = new(2, 978, "\u20AC"),
        ["GBP"] = new(2, 826, "\u00A3"),
        ["INR"] = new(2, 356, "\u20B9"),
        ["JOD"] = new(3, 400, "JD"),
        ["JPY"] = new(0, 392, "\u00A5"),
        ["KRW"] = new(0, 410, "\u20A9"),
        ["KWD"] = new(3, 414, "KD"),
        ["MXN"] = new(2, 484, "MX$"),
        ["PYG"] = new(0, 600, "\u20B2"),
        ["USD"] = new(2, 840, "$"),
        ["UYU"] = new(2, 858, "$U"),
    };

    private readonly string? _code;

    /// <summary>Wraps an alphabetic code without validating it; use <see cref="Parse"/> to validate.</summary>
    /// <param name="code">The code, e.g. <c>"BRL"</c>.</param>
    public Currency(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        _code = code;
    }

    /// <summary>Brazilian real (exponent 2).</summary>
    public static Currency BRL { get; } = new("BRL");

    /// <summary>US dollar (exponent 2).</summary>
    public static Currency USD { get; } = new("USD");

    /// <summary>Euro (exponent 2).</summary>
    public static Currency EUR { get; } = new("EUR");

    /// <summary>Pound sterling (exponent 2).</summary>
    public static Currency GBP { get; } = new("GBP");

    /// <summary>Japanese yen (exponent 0).</summary>
    public static Currency JPY { get; } = new("JPY");

    /// <summary>The alphabetic code; empty for the default value.</summary>
    public string Code => _code ?? string.Empty;

    /// <summary>Whether the code is in the currency table.</summary>
    public bool IsValid => Table.ContainsKey(Code);

    /// <summary>The number of minor-unit digits (2 for BRL, 0 for JPY, 3 for KWD), or -1 if the code is unknown.</summary>
    public int Exponent => Table.TryGetValue(Code, out Info info) ? info.Exponent : -1;

    /// <summary>The ISO 4217 numeric code (986 for BRL, the value of field 53 of a Pix BR Code), or 0 if unknown.</summary>
    public int NumericCode => Table.TryGetValue(Code, out Info info) ? info.Numeric : 0;

    /// <summary>The display symbol (<c>"R$"</c> for BRL), or the code itself if unknown.</summary>
    public string Symbol => Table.TryGetValue(Code, out Info info) ? info.Symbol : Code;

    /// <summary>Parses an alphabetic code in any letter case, ignoring surrounding white space.</summary>
    /// <param name="code">The code to parse.</param>
    /// <returns>The currency.</returns>
    /// <exception cref="MoneyException">The code is not in the table (<see cref="MoneyError.UnknownCurrency"/>).</exception>
    public static Currency Parse(string code)
    {
        if (!TryParse(code, out Currency currency))
        {
            throw new MoneyException(MoneyError.UnknownCurrency, code);
        }

        return currency;
    }

    /// <summary>Parses an alphabetic code in any letter case, ignoring surrounding white space.</summary>
    /// <param name="code">The code to parse.</param>
    /// <param name="currency">The currency, when the method returns true.</param>
    /// <returns>Whether the code is in the table.</returns>
    public static bool TryParse(string? code, out Currency currency)
    {
        currency = default;
        if (code is null)
        {
            return false;
        }

        var candidate = new Currency(code.Trim().ToUpperInvariant());
        if (!candidate.IsValid)
        {
            return false;
        }

        currency = candidate;
        return true;
    }

    /// <summary>Returns the alphabetic code.</summary>
    /// <returns>The code.</returns>
    public override string ToString() => Code;

    /// <summary>Compares codes ordinally.</summary>
    /// <param name="other">The currency to compare with.</param>
    /// <returns>Whether the codes are equal.</returns>
    public bool Equals(Currency other) => string.Equals(Code, other.Code, StringComparison.Ordinal);

    /// <summary>Compares codes ordinally.</summary>
    /// <param name="obj">The object to compare with.</param>
    /// <returns>Whether <paramref name="obj"/> is a currency with the same code.</returns>
    public override bool Equals(object? obj) => obj is Currency other && Equals(other);

    /// <summary>Returns a hash of the code.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Code);

    /// <summary>Orders currencies by code, byte-wise on the UTF-8 encoding.</summary>
    /// <param name="other">The currency to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(Currency other) => Utf8Order.CompareStrings(Code, other.Code);

    /// <summary>Whether two currencies have the same code.</summary>
    /// <param name="left">The first currency.</param>
    /// <param name="right">The second currency.</param>
    /// <returns>Whether the codes are equal.</returns>
    public static bool operator ==(Currency left, Currency right) => left.Equals(right);

    /// <summary>Whether two currencies have different codes.</summary>
    /// <param name="left">The first currency.</param>
    /// <param name="right">The second currency.</param>
    /// <returns>Whether the codes differ.</returns>
    public static bool operator !=(Currency left, Currency right) => !left.Equals(right);

    private readonly record struct Info(int Exponent, int Numeric, string Symbol);
}
