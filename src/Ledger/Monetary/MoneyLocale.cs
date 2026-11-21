namespace Ledger.Monetary;

/// <summary>
/// Controls how <see cref="Money"/> is rendered for humans. Formatting is
/// implemented digit by digit and never consults <c>CultureInfo</c>, so output
/// is identical under invariant globalization, on every OS, and in the Go
/// implementation.
/// </summary>
public sealed record MoneyLocale
{
    /// <summary>Brazilian Portuguese: <c>"R$ 1.234,56"</c>, <c>"-R$ 0,01"</c>.</summary>
    public static MoneyLocale PtBR { get; } = new() { DecimalSeparator = ",", GroupSeparator = ".", SymbolSpace = true };

    /// <summary>United States English: <c>"$1,234.56"</c>.</summary>
    public static MoneyLocale EnUS { get; } = new() { DecimalSeparator = ".", GroupSeparator = ",", SymbolSpace = false };

    /// <summary>Separates the integer and fractional parts.</summary>
    public string DecimalSeparator { get; init; } = ".";

    /// <summary>Separates thousands in the integer part; empty disables grouping.</summary>
    public string GroupSeparator { get; init; } = string.Empty;

    /// <summary>Whether a space separates the currency symbol from the number.</summary>
    public bool SymbolSpace { get; init; }
}
