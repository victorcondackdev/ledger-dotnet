using System.Globalization;
using System.Text;

namespace Ledger.Monetary;

/// <summary>
/// An exact monetary amount: a signed 64-bit number of minor units (cents,
/// centavos, yen) of a <see cref="Ledger.Monetary.Currency"/>. No floating point is
/// involved anywhere: amounts are parsed and formatted digit by digit,
/// arithmetic is overflow-checked, and <see cref="Allocate"/> splits an
/// amount without creating or losing a single minor unit.
/// </summary>
/// <remarks>
/// The default value has no currency and is mostly useful as a "not set"
/// marker. Equality compares both the minor units and the currency.
/// </remarks>
public readonly record struct Money : IComparable<Money>
{
    /// <summary>Creates an amount of <paramref name="minorUnits"/> of <paramref name="currency"/>; <c>new Money(1050, Currency.BRL)</c> is R$ 10,50. The currency is not validated.</summary>
    /// <param name="minorUnits">The amount in minor units.</param>
    /// <param name="currency">The currency.</param>
    public Money(long minorUnits, Currency currency)
    {
        MinorUnits = minorUnits;
        Currency = currency;
    }

    /// <summary>The amount in minor units.</summary>
    public long MinorUnits { get; init; }

    /// <summary>The currency.</summary>
    public Currency Currency { get; init; }

    /// <summary>Whether the amount is zero (in any currency).</summary>
    public bool IsZero => MinorUnits == 0;

    /// <summary>Whether the amount is greater than zero.</summary>
    public bool IsPositive => MinorUnits > 0;

    /// <summary>Whether the amount is less than zero.</summary>
    public bool IsNegative => MinorUnits < 0;

    /// <summary>-1, 0 or +1.</summary>
    public int Sign => Math.Sign(MinorUnits);

    /// <summary>The zero amount of a currency.</summary>
    /// <param name="currency">The currency.</param>
    /// <returns>Zero minor units of <paramref name="currency"/>.</returns>
    public static Money Zero(Currency currency) => new(0, currency);

    /// <summary>Parses a plain decimal amount: an optional sign, at least one digit and optionally a dot followed by 1 to <c>Currency.Exponent</c> digits. It never rounds.</summary>
    /// <param name="currency">The currency.</param>
    /// <param name="text">Text such as <c>"10"</c>, <c>"10.5"</c>, <c>"-0.01"</c> or <c>"+3.00"</c>.</param>
    /// <returns>The amount.</returns>
    /// <exception cref="MoneyException">The currency is unknown, or the text is malformed, too precise or out of range.</exception>
    public static Money Parse(Currency currency, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        MoneyError error = TryParseCore(currency, text, out Money value);
        if (error != 0)
        {
            throw new MoneyException(error, error == MoneyError.UnknownCurrency ? currency.Code : Quote(text));
        }

        return value;
    }

    /// <summary>Parses a plain decimal amount without throwing; see <c>Parse</c>.</summary>
    /// <param name="currency">The currency.</param>
    /// <param name="text">The text to parse.</param>
    /// <param name="value">The amount, when the method returns true.</param>
    /// <returns>Whether the text is a valid amount.</returns>
    public static bool TryParse(Currency currency, string? text, out Money value)
    {
        value = default;
        return text is not null && TryParseCore(currency, text, out value) == 0;
    }

    /// <summary>
    /// Parses an amount formatted for humans, the inverse of <c>Format</c>:
    /// <c>"R$ 1.234,56"</c>, <c>"1.234,56"</c>, <c>"-R$ 0,01"</c> and
    /// <c>"R$1234,56"</c> are valid with <see cref="MoneyLocale.PtBR"/>. The
    /// currency symbol is optional; group separators must split the integer
    /// part into groups of three.
    /// </summary>
    /// <param name="currency">The currency.</param>
    /// <param name="text">The text to parse.</param>
    /// <param name="locale">The separators to expect.</param>
    /// <returns>The amount.</returns>
    /// <exception cref="MoneyException">The currency is unknown, or the text is malformed, too precise or out of range.</exception>
    public static Money ParseLocale(Currency currency, string text, MoneyLocale locale)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(locale);
        MoneyError error = TryParseLocaleCore(currency, text, locale, out Money value);
        if (error != 0)
        {
            throw new MoneyException(error, error == MoneyError.UnknownCurrency ? currency.Code : Quote(text));
        }

        return value;
    }

    /// <summary>Parses an amount formatted for humans without throwing; see <c>ParseLocale</c>.</summary>
    /// <param name="currency">The currency.</param>
    /// <param name="text">The text to parse.</param>
    /// <param name="locale">The separators to expect.</param>
    /// <param name="value">The amount, when the method returns true.</param>
    /// <returns>Whether the text is a valid amount.</returns>
    public static bool TryParseLocale(Currency currency, string? text, MoneyLocale locale, out Money value)
    {
        ArgumentNullException.ThrowIfNull(locale);
        value = default;
        return text is not null && TryParseLocaleCore(currency, text, locale, out value) == 0;
    }

    /// <summary>Whether both amounts have the same currency.</summary>
    /// <param name="other">The other amount.</param>
    /// <returns>Whether the currencies are equal.</returns>
    public bool SameCurrency(Money other) => Currency == other.Currency;

    /// <summary>Returns this + <paramref name="other"/>.</summary>
    /// <param name="other">The amount to add, in the same currency.</param>
    /// <returns>The sum.</returns>
    /// <exception cref="MoneyException">The currencies differ, or the sum overflows.</exception>
    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        try
        {
            return new Money(checked(MinorUnits + other.MinorUnits), Currency);
        }
        catch (OverflowException e)
        {
            throw new MoneyException(MoneyError.Overflow, null, e);
        }
    }

    /// <summary>Returns this - <paramref name="other"/>.</summary>
    /// <param name="other">The amount to subtract, in the same currency.</param>
    /// <returns>The difference.</returns>
    /// <exception cref="MoneyException">The currencies differ, or the difference overflows.</exception>
    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        try
        {
            return new Money(checked(MinorUnits - other.MinorUnits), Currency);
        }
        catch (OverflowException e)
        {
            throw new MoneyException(MoneyError.Overflow, null, e);
        }
    }

    /// <summary>Returns -this. Fails only for <see cref="long.MinValue"/> minor units.</summary>
    /// <returns>The negated amount.</returns>
    /// <exception cref="MoneyException">The negation overflows.</exception>
    public Money Negate()
    {
        if (MinorUnits == long.MinValue)
        {
            throw new MoneyException(MoneyError.Overflow);
        }

        return new Money(-MinorUnits, Currency);
    }

    /// <summary>Returns |this|. Fails only for <see cref="long.MinValue"/> minor units.</summary>
    /// <returns>The absolute value.</returns>
    /// <exception cref="MoneyException">The result overflows.</exception>
    public Money Abs() => MinorUnits < 0 ? Negate() : this;

    /// <summary>Returns this * <paramref name="factor"/>, e.g. a unit price times a quantity.</summary>
    /// <param name="factor">The multiplier.</param>
    /// <returns>The product.</returns>
    /// <exception cref="MoneyException">The product overflows.</exception>
    public Money Multiply(long factor)
    {
        try
        {
            return new Money(checked(MinorUnits * factor), Currency);
        }
        catch (OverflowException e)
        {
            throw new MoneyException(MoneyError.Overflow, null, e);
        }
    }

    /// <summary>Compares two amounts of the same currency.</summary>
    /// <param name="other">The amount to compare with.</param>
    /// <returns>-1, 0 or +1.</returns>
    /// <exception cref="MoneyException">The currencies differ.</exception>
    public int CompareTo(Money other)
    {
        EnsureSameCurrency(other);
        return MinorUnits.CompareTo(other.MinorUnits);
    }

    /// <summary>
    /// Splits the amount into <c>ratios.Length</c> parts proportional to
    /// <paramref name="ratios"/> with the largest remainder method: every part
    /// gets the floor of its exact share and the minor units left over go, one
    /// each, to the parts with the largest remainders (ties broken by
    /// position). The parts always sum exactly to this amount and none differs
    /// from its exact share by one minor unit or more. Intermediate products
    /// use 128-bit arithmetic, so no input overflows.
    /// </summary>
    /// <remarks>A R$ 0,05 fee split 70/30 gives R$ 0,04 and R$ 0,01. Negative amounts are split by magnitude and keep their sign; a zero ratio yields a zero part.</remarks>
    /// <param name="ratios">Non-negative ratios with a positive sum.</param>
    /// <returns>The parts, in ratio order.</returns>
    /// <exception cref="MoneyException">The ratios are empty, negative, sum to zero or overflow (<see cref="MoneyError.Ratios"/>).</exception>
    public Money[] Allocate(params long[] ratios)
    {
        ArgumentNullException.ThrowIfNull(ratios);
        if (ratios.Length == 0)
        {
            throw new MoneyException(MoneyError.Ratios);
        }

        ulong total = 0;
        foreach (long r in ratios)
        {
            if (r < 0)
            {
                throw new MoneyException(MoneyError.Ratios);
            }

            // Each ratio is at most long.MaxValue and so is the running total,
            // so the ulong sum cannot wrap before the check.
            total += (ulong)r;
            if (total > long.MaxValue)
            {
                throw new MoneyException(MoneyError.Ratios);
            }
        }

        if (total == 0)
        {
            throw new MoneyException(MoneyError.Ratios);
        }

        bool negative = MinorUnits < 0;
        ulong magnitude = Magnitude(MinorUnits);
        int n = ratios.Length;
        var shares = new ulong[n];
        var remainders = new ulong[n];
        ulong assigned = 0;
        for (int i = 0; i < n; i++)
        {
            // magnitude * ratio / total fits in 64 bits because ratio <= total.
            UInt128 product = (UInt128)magnitude * (ulong)ratios[i];
            shares[i] = (ulong)(product / total);
            remainders[i] = (ulong)(product % total);
            assigned += shares[i];
        }

        int left = (int)(magnitude - assigned); // fewer than n
        if (left > 0)
        {
            var order = new int[n];
            for (int i = 0; i < n; i++)
            {
                order[i] = i;
            }

            Array.Sort(order, (x, y) =>
            {
                int byRemainder = remainders[y].CompareTo(remainders[x]);
                return byRemainder != 0 ? byRemainder : x.CompareTo(y);
            });
            for (int k = 0; k < left; k++)
            {
                shares[order[k]]++;
            }
        }

        var parts = new Money[n];
        for (int i = 0; i < n; i++)
        {
            long v = unchecked((long)shares[i]);
            parts[i] = new Money(negative ? unchecked(-v) : v, Currency);
        }

        return parts;
    }

    /// <summary>Divides the amount into <paramref name="parts"/> parts as equal as possible, earlier parts getting the extra minor units: R$ 10,00 / 3 = 3,34 + 3,33 + 3,33.</summary>
    /// <param name="parts">The number of parts, at least 1.</param>
    /// <returns>The parts.</returns>
    /// <exception cref="MoneyException"><paramref name="parts"/> is not positive.</exception>
    public Money[] Split(int parts)
    {
        if (parts <= 0)
        {
            throw new MoneyException(MoneyError.Ratios);
        }

        var ratios = new long[parts];
        Array.Fill(ratios, 1L);
        return Allocate(ratios);
    }

    /// <summary>
    /// Returns the amount as a plain decimal with exactly the currency's number
    /// of fraction digits and no grouping: <c>"1234.56"</c>, <c>"-0.05"</c>,
    /// <c>"1500"</c> (JPY). Unknown currencies use exponent 0. The result is
    /// accepted by <c>Parse</c>.
    /// </summary>
    /// <returns>The decimal string.</returns>
    public string ToDecimalString() => Render(".", string.Empty);

    /// <summary>Renders the amount for display with the currency symbol: <see cref="MoneyLocale.PtBR"/> gives <c>"R$ 1.234,56"</c>. The result is accepted by <c>ParseLocale</c> with the same locale.</summary>
    /// <param name="locale">The separators to use.</param>
    /// <returns>The formatted amount.</returns>
    public string Format(MoneyLocale locale)
    {
        ArgumentNullException.ThrowIfNull(locale);
        string number = Render(locale.DecimalSeparator, locale.GroupSeparator);
        var b = new StringBuilder(number.Length + 8);
        if (number.StartsWith('-'))
        {
            b.Append('-');
            number = number.Substring(1);
        }

        string symbol = Currency.Symbol;
        b.Append(symbol);
        if (locale.SymbolSpace && symbol.Length > 0)
        {
            b.Append(' ');
        }

        return b.Append(number).ToString();
    }

    /// <summary>Returns the code and decimal value, e.g. <c>"BRL 1234.56"</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        string code = Currency.Code;
        return code.Length == 0 ? ToDecimalString() : code + " " + ToDecimalString();
    }

    /// <summary>Adds two amounts of the same currency.</summary>
    /// <param name="left">The first amount.</param>
    /// <param name="right">The second amount.</param>
    /// <returns>The sum.</returns>
    /// <exception cref="MoneyException">The currencies differ, or the sum overflows.</exception>
    public static Money operator +(Money left, Money right) => left.Add(right);

    /// <summary>Subtracts two amounts of the same currency.</summary>
    /// <param name="left">The minuend.</param>
    /// <param name="right">The subtrahend.</param>
    /// <returns>The difference.</returns>
    /// <exception cref="MoneyException">The currencies differ, or the difference overflows.</exception>
    public static Money operator -(Money left, Money right) => left.Subtract(right);

    /// <summary>Negates an amount.</summary>
    /// <param name="value">The amount.</param>
    /// <returns>The negated amount.</returns>
    /// <exception cref="MoneyException">The negation overflows.</exception>
    public static Money operator -(Money value) => value.Negate();

    /// <summary>Multiplies an amount by an integer.</summary>
    /// <param name="value">The amount.</param>
    /// <param name="factor">The multiplier.</param>
    /// <returns>The product.</returns>
    /// <exception cref="MoneyException">The product overflows.</exception>
    public static Money operator *(Money value, long factor) => value.Multiply(factor);

    /// <summary>Whether <paramref name="left"/> is less than <paramref name="right"/>.</summary>
    /// <param name="left">The first amount.</param>
    /// <param name="right">The second amount, in the same currency.</param>
    /// <returns>The comparison result.</returns>
    /// <exception cref="MoneyException">The currencies differ.</exception>
    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    /// <summary>Whether <paramref name="left"/> is greater than <paramref name="right"/>.</summary>
    /// <param name="left">The first amount.</param>
    /// <param name="right">The second amount, in the same currency.</param>
    /// <returns>The comparison result.</returns>
    /// <exception cref="MoneyException">The currencies differ.</exception>
    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    /// <summary>Whether <paramref name="left"/> is less than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The first amount.</param>
    /// <param name="right">The second amount, in the same currency.</param>
    /// <returns>The comparison result.</returns>
    /// <exception cref="MoneyException">The currencies differ.</exception>
    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    /// <summary>Whether <paramref name="left"/> is greater than or equal to <paramref name="right"/>.</summary>
    /// <param name="left">The first amount.</param>
    /// <param name="right">The second amount, in the same currency.</param>
    /// <returns>The comparison result.</returns>
    /// <exception cref="MoneyException">The currencies differ.</exception>
    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
        {
            throw new MoneyException(MoneyError.CurrencyMismatch, Currency.Code + " and " + other.Currency.Code);
        }
    }

    private static ulong Magnitude(long v) => v < 0 ? unchecked(0UL - (ulong)v) : (ulong)v;

    private string Render(string decimalSeparator, string groupSeparator)
    {
        int exp = Math.Max(Currency.Exponent, 0);
        bool negative = MinorUnits < 0;
        string digits = Magnitude(MinorUnits).ToString(CultureInfo.InvariantCulture);
        if (digits.Length <= exp)
        {
            digits = new string('0', exp - digits.Length + 1) + digits;
        }

        int intLength = digits.Length - exp;
        var b = new StringBuilder(digits.Length + (digits.Length / 3 * groupSeparator.Length) + 2);
        if (negative)
        {
            b.Append('-');
        }

        if (groupSeparator.Length == 0)
        {
            b.Append(digits, 0, intLength);
        }
        else
        {
            int first = intLength % 3;
            if (first == 0)
            {
                first = 3;
            }

            b.Append(digits, 0, first);
            for (int i = first; i < intLength; i += 3)
            {
                b.Append(groupSeparator).Append(digits, i, 3);
            }
        }

        if (exp > 0)
        {
            b.Append(decimalSeparator).Append(digits, intLength, exp);
        }

        return b.ToString();
    }

    private static string Quote(string text) => "\"" + text + "\"";

    private static MoneyError TryParseCore(Currency currency, string text, out Money value)
    {
        value = default;
        int exp = currency.Exponent;
        if (exp < 0)
        {
            return MoneyError.UnknownCurrency;
        }

        MoneyError error = TryParseMinor(text, ".", exp, out long minor);
        if (error == 0)
        {
            value = new Money(minor, currency);
        }

        return error;
    }

    private static MoneyError TryParseLocaleCore(Currency currency, string text, MoneyLocale locale, out Money value)
    {
        value = default;
        int exp = currency.Exponent;
        if (exp < 0)
        {
            return MoneyError.UnknownCurrency;
        }

        string s = text.Trim();
        string sign = string.Empty;
        if (s.StartsWith('-') || s.StartsWith('+'))
        {
            sign = s.Substring(0, 1);
            s = s.Substring(1);
        }

        string symbol = currency.Symbol;
        if (s.StartsWith(symbol, StringComparison.Ordinal))
        {
            s = s.Substring(symbol.Length);
            if (s.StartsWith(' '))
            {
                s = s.Substring(1);
            }
        }

        string group = locale.GroupSeparator;
        string dec = locale.DecimalSeparator;
        if (group.Length > 0 && s.Contains(group, StringComparison.Ordinal))
        {
            string intPart = s;
            string frac = string.Empty;
            if (dec.Length > 0)
            {
                int i = s.IndexOf(dec, StringComparison.Ordinal);
                if (i >= 0)
                {
                    intPart = s.Substring(0, i);
                    frac = s.Substring(i);
                }
            }

            string[] groups = intPart.Split(group);
            for (int i = 0; i < groups.Length; i++)
            {
                // Group lengths are measured in UTF-8 bytes so that malformed
                // input fails with the same error as the Go implementation.
                int length = Encoding.UTF8.GetByteCount(groups[i]);
                if ((i == 0 && (length == 0 || length > 3)) || (i > 0 && length != 3))
                {
                    return MoneyError.Syntax;
                }
            }

            s = string.Concat(groups) + frac;
        }

        MoneyError error = TryParseMinor(sign + s, dec, exp, out long minor);
        if (error == 0)
        {
            value = new Money(minor, currency);
        }

        return error;
    }

    private static MoneyError TryParseMinor(string s, string dec, int exp, out long value)
    {
        value = 0;
        bool negative = false;
        if (s.Length > 0 && (s[0] == '-' || s[0] == '+'))
        {
            negative = s[0] == '-';
            s = s.Substring(1);
        }

        string intPart = s;
        string frac = string.Empty;
        bool hasSeparator = false;
        if (dec.Length > 0)
        {
            int i = s.IndexOf(dec, StringComparison.Ordinal);
            if (i >= 0)
            {
                intPart = s.Substring(0, i);
                frac = s.Substring(i + dec.Length);
                hasSeparator = true;
            }
        }

        if (intPart.Length == 0 || (hasSeparator && frac.Length == 0))
        {
            return MoneyError.Syntax;
        }

        if (Encoding.UTF8.GetByteCount(frac) > exp)
        {
            return AllDigits(frac) ? MoneyError.Precision : MoneyError.Syntax;
        }

        if (!AllDigits(intPart) || !AllDigits(frac))
        {
            return MoneyError.Syntax;
        }

        // Accumulate as a negative number so that long.MinValue is representable.
        long v = 0;
        string digits = intPart + frac + new string('0', exp - frac.Length);
        foreach (char c in digits)
        {
            if (!CheckedMath.TryMultiply(v, 10, out v) || !CheckedMath.TrySubtract(v, c - '0', out v))
            {
                return MoneyError.Overflow;
            }
        }

        if (!negative)
        {
            if (v == long.MinValue)
            {
                return MoneyError.Overflow;
            }

            v = -v;
        }

        value = v;
        return 0;
    }

    private static bool AllDigits(string s)
    {
        foreach (char c in s)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return true;
    }
}
