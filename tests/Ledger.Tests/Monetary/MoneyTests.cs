using System.Numerics;
using Ledger.Monetary;

namespace Ledger.Tests;

public class MoneyTests
{
    private static readonly Currency Kwd = new("KWD");

    private static Money Brl(long minor) => new(minor, Currency.BRL);

    private static MoneyError ErrorOf(Action action) => Assert.Throws<MoneyException>(action).Code;

    [Fact]
    public void CurrencyTable()
    {
        foreach ((string code, int exponent, int numeric, string symbol) in new[]
        {
            ("BRL", 2, 986, "R$"), ("brl", 2, 986, "R$"), (" usd ", 2, 840, "$"), ("JPY", 0, 392, "\u00A5"),
            ("KWD", 3, 414, "KD"), ("CLF", 4, 990, "UF"),
        })
        {
            Currency c = Currency.Parse(code);
            Assert.True(c.IsValid);
            Assert.Equal(exponent, c.Exponent);
            Assert.Equal(numeric, c.NumericCode);
            Assert.Equal(symbol, c.Symbol);
        }

        Assert.Equal(MoneyError.UnknownCurrency, ErrorOf(() => Currency.Parse("XXX")));
        Assert.False(Currency.TryParse("", out _));
        Assert.False(Currency.TryParse(null, out _));
        var unknown = new Currency("ZZZ");
        Assert.Equal(-1, unknown.Exponent);
        Assert.Equal(0, unknown.NumericCode);
        Assert.Equal("ZZZ", unknown.Symbol);
        Assert.Equal("ZZZ", unknown.ToString());
        Assert.Equal(new Currency(""), default(Currency));
        Assert.Equal(string.Empty, default(Currency).Code);
    }

    [Fact]
    public void ArithmeticIsChecked()
    {
        Money a = Brl(150);
        Money b = Brl(-50);
        var usd = new Money(1, Currency.USD);
        Money max = Brl(long.MaxValue);
        Money min = Brl(long.MinValue);

        Assert.Equal(Brl(100), a + b);
        Assert.Equal(Brl(200), a - b);
        Assert.Equal(Brl(-450), a * -3);
        Assert.Equal(Brl(50), -b);
        Assert.Equal(Brl(50), b.Abs());
        Assert.Equal(a, a.Abs());

        var overflows = new (string Name, Action Action)[]
        {
            ("add overflow", () => _ = max + Brl(1)),
            ("add underflow", () => _ = min + Brl(-1)),
            ("sub overflow", () => _ = max - Brl(-1)),
            ("sub underflow", () => _ = min - Brl(1)),
            ("neg min", () => _ = -min),
            ("abs min", () => _ = min.Abs()),
            ("mul overflow", () => _ = max * 2),
            ("mul min by -1", () => _ = min * -1),
            ("mul -1 by min", () => _ = Brl(-1) * long.MinValue),
            ("mul big", () => _ = Brl((long.MaxValue / 2) + 1) * 2),
            ("mul neg big", () => _ = Brl(-((long.MaxValue / 3) + 1)) * -3),
            ("mul square", () => _ = Brl(3_037_000_500) * 3_037_000_500),
        };
        foreach ((string name, Action action) in overflows)
        {
            Assert.True(ErrorOf(action) == MoneyError.Overflow, name);
        }

        Assert.Equal(MoneyError.CurrencyMismatch, ErrorOf(() => _ = a + usd));
        Assert.Equal(MoneyError.CurrencyMismatch, ErrorOf(() => _ = a - usd));
        Assert.Equal(MoneyError.CurrencyMismatch, ErrorOf(() => _ = a.CompareTo(usd)));
        Assert.Equal("money: currency mismatch: BRL and USD", Assert.Throws<MoneyException>(() => _ = a + usd).Message);
        Assert.True((Brl(0) * long.MinValue).IsZero);

        Assert.Equal(1, a.CompareTo(b));
        Assert.Equal(-1, b.CompareTo(a));
        Assert.Equal(0, a.CompareTo(a));
        Assert.True(b < a && a > b && b <= a && a >= b);
        Assert.Equal(1, a.Sign);
        Assert.Equal(-1, b.Sign);
        Assert.Equal(0, Money.Zero(Currency.BRL).Sign);
        Assert.True(a.IsPositive && b.IsNegative && Money.Zero(Currency.BRL).IsZero && !a.IsNegative);
        Assert.True(a.SameCurrency(b) && !a.SameCurrency(usd));
        Assert.Equal(150, a.MinorUnits);
        Assert.Equal(Currency.BRL, a.Currency);
    }

    [Fact]
    public void ParseIsExactAndNeverRounds()
    {
        var cases = new (Currency Currency, string Input, long Minor, MoneyError? Error)[]
        {
            (Currency.BRL, "0", 0, null),
            (Currency.BRL, "10", 1000, null),
            (Currency.BRL, "10.5", 1050, null),
            (Currency.BRL, "-0.01", -1, null),
            (Currency.BRL, "+3.00", 300, null),
            (Currency.BRL, "007.10", 710, null),
            (Currency.BRL, "92233720368547758.07", long.MaxValue, null),
            (Currency.BRL, "-92233720368547758.08", long.MinValue, null),
            (Currency.BRL, "92233720368547758.08", 0, MoneyError.Overflow),
            (Currency.BRL, "-92233720368547758.09", 0, MoneyError.Overflow),
            (Currency.JPY, "1500", 1500, null),
            (Currency.JPY, "1.5", 0, MoneyError.Precision),
            (Kwd, "1.234", 1234, null),
            (Currency.BRL, "1.234", 0, MoneyError.Precision),
            (Currency.BRL, "1.2x4", 0, MoneyError.Syntax),
            (Currency.BRL, "", 0, MoneyError.Syntax),
            (Currency.BRL, ".5", 0, MoneyError.Syntax),
            (Currency.BRL, "5.", 0, MoneyError.Syntax),
            (Currency.BRL, "10,50", 0, MoneyError.Syntax),
            (Currency.BRL, "1e3", 0, MoneyError.Syntax),
            (Currency.BRL, "--1", 0, MoneyError.Syntax),
            (new Currency("XXX"), "1", 0, MoneyError.UnknownCurrency),
        };
        foreach ((Currency currency, string input, long minor, MoneyError? error) in cases)
        {
            if (error is null)
            {
                Assert.Equal(new Money(minor, currency), Money.Parse(currency, input));
                Assert.True(Money.TryParse(currency, input, out Money parsed) && parsed.MinorUnits == minor);
            }
            else
            {
                Assert.True(ErrorOf(() => Money.Parse(currency, input)) == error, input);
                Assert.False(Money.TryParse(currency, input, out _));
            }
        }

        Assert.Equal("money: too many decimal places: \"0.001\"", Assert.Throws<MoneyException>(() => Money.Parse(Currency.BRL, "0.001")).Message);
    }

    [Fact]
    public void FormatsWithoutCultureInfo()
    {
        var cases = new (Money Amount, string Decimal, string PtBR, string EnUS, string Text)[]
        {
            (Brl(123456), "1234.56", "R$ 1.234,56", "R$1,234.56", "BRL 1234.56"),
            (Brl(-1), "-0.01", "-R$ 0,01", "-R$0.01", "BRL -0.01"),
            (Brl(0), "0.00", "R$ 0,00", "R$0.00", "BRL 0.00"),
            (new Money(100000000, Currency.USD), "1000000.00", "$ 1.000.000,00", "$1,000,000.00", "USD 1000000.00"),
            (new Money(1234567, Currency.JPY), "1234567", "\u00A5 1.234.567", "\u00A51,234,567", "JPY 1234567"),
            (new Money(5, Kwd), "0.005", "KD 0,005", "KD0.005", "KWD 0.005"),
            (Brl(long.MinValue), "-92233720368547758.08", "-R$ 92.233.720.368.547.758,08", "-R$92,233,720,368,547,758.08", "BRL -92233720368547758.08"),
            (new Money(42, default), "42", "42", "42", "42"),
        };
        foreach ((Money amount, string dec, string ptBR, string enUS, string text) in cases)
        {
            Assert.Equal(dec, amount.ToDecimalString());
            Assert.Equal(ptBR, amount.Format(MoneyLocale.PtBR));
            Assert.Equal(enUS, amount.Format(MoneyLocale.EnUS));
            Assert.Equal(text, amount.ToString());
        }
    }

    [Fact]
    public void ParseLocaleInvertsFormat()
    {
        var cases = new (string Input, MoneyLocale Locale, long Minor, MoneyError? Error)[]
        {
            ("R$ 1.234,56", MoneyLocale.PtBR, 123456, null),
            ("1.234,56", MoneyLocale.PtBR, 123456, null),
            ("R$1234,56", MoneyLocale.PtBR, 123456, null),
            ("-R$ 0,01", MoneyLocale.PtBR, -1, null),
            ("  R$ 10  ", MoneyLocale.PtBR, 1000, null),
            ("R$ 1.000.000,00", MoneyLocale.PtBR, 100000000, null),
            ("R$1,234.56", MoneyLocale.EnUS, 123456, null),
            ("R$ 12.34,56", MoneyLocale.PtBR, 0, MoneyError.Syntax),
            ("R$ .234,56", MoneyLocale.PtBR, 0, MoneyError.Syntax),
            ("R$ 1234.567,00", MoneyLocale.PtBR, 0, MoneyError.Syntax),
            ("R$ 1,234", MoneyLocale.PtBR, 0, MoneyError.Precision),
            ("US$ 1,00", MoneyLocale.PtBR, 0, MoneyError.Syntax),
        };
        foreach ((string input, MoneyLocale locale, long minor, MoneyError? error) in cases)
        {
            if (error is null)
            {
                Assert.Equal(minor, Money.ParseLocale(Currency.BRL, input, locale).MinorUnits);
            }
            else
            {
                Assert.True(ErrorOf(() => Money.ParseLocale(Currency.BRL, input, locale)) == error, input);
            }
        }

        Assert.Equal(MoneyError.UnknownCurrency, ErrorOf(() => Money.ParseLocale(new Currency("XXX"), "1", MoneyLocale.PtBR)));
    }

    [Fact]
    public void AllocateUsesLargestRemainder()
    {
        var cases = new (long Minor, long[] Ratios, long[] Want)[]
        {
            (100, new long[] { 1, 1, 1 }, new long[] { 34, 33, 33 }),
            (-100, new long[] { 1, 1, 1 }, new long[] { -34, -33, -33 }),
            (99, new long[] { 70, 30 }, new long[] { 69, 30 }),
            (5, new long[] { 70, 30 }, new long[] { 4, 1 }),
            (1, new long[] { 1, 1 }, new long[] { 1, 0 }),
            (0, new long[] { 1, 2 }, new long[] { 0, 0 }),
            (100, new long[] { 0, 1 }, new long[] { 0, 100 }),
            (10, new long[] { 1, 2, 7 }, new long[] { 1, 2, 7 }),
            (2, new long[] { 1, 1, 1 }, new long[] { 1, 1, 0 }),
            (long.MaxValue, new long[] { 1, 1 }, new long[] { (long.MaxValue / 2) + 1, long.MaxValue / 2 }),
            (long.MinValue, new long[] { 1, 1 }, new long[] { long.MinValue / 2, long.MinValue / 2 }),
            (long.MinValue, new long[] { 1 }, new long[] { long.MinValue }),
            (long.MaxValue, new long[] { long.MaxValue - 1, 1 }, new long[] { long.MaxValue - 1, 1 }),
        };
        foreach ((long minor, long[] ratios, long[] want) in cases)
        {
            Money[] parts = Brl(minor).Allocate(ratios);
            Assert.Equal(want, parts.Select(p => p.MinorUnits));
            Assert.All(parts, p => Assert.Equal(Currency.BRL, p.Currency));
        }

        foreach (long[] ratios in new[] { Array.Empty<long>(), new long[] { 0, 0 }, new long[] { -1, 2 }, new long[] { long.MaxValue, 1 } })
        {
            Assert.Equal(MoneyError.Ratios, ErrorOf(() => Brl(1).Allocate(ratios)));
        }

        Assert.Equal(new long[] { 334, 333, 333 }, Brl(1000).Split(3).Select(p => p.MinorUnits));
        Assert.Equal(MoneyError.Ratios, ErrorOf(() => Brl(1).Split(0)));
    }

    [Fact]
    public void AllocatePropertiesHoldForRandomInputs()
    {
        var rng = new Random(20261121);
        for (int iteration = 0; iteration < 5000; iteration++)
        {
            long minor = rng.Next(4) switch
            {
                0 => rng.NextInt64(long.MinValue, long.MaxValue),
                1 => rng.NextInt64(-1000, 1000),
                2 => long.MinValue,
                _ => long.MaxValue,
            };
            int n = 1 + rng.Next(5);
            var ratios = new long[n];
            for (int i = 0; i < n; i++)
            {
                ratios[i] = rng.Next(3) == 0 ? rng.Next(3) : rng.NextInt64(long.MaxValue / n);
            }

            BigInteger total = ratios.Aggregate(BigInteger.Zero, (acc, r) => acc + r);
            if (total.IsZero)
            {
                continue;
            }

            Money[] parts = Brl(minor).Allocate(ratios);
            BigInteger sum = BigInteger.Zero;
            for (int i = 0; i < n; i++)
            {
                long part = parts[i].MinorUnits;
                Assert.False((minor < 0 && part > 0) || (minor > 0 && part < 0), "part has the wrong sign");
                sum += part;

                // |part - exact share| < 1 minor unit, i.e. |part * total - minor * ratio| < total.
                BigInteger diff = BigInteger.Abs((part * total) - ((BigInteger)minor * ratios[i]));
                Assert.True(diff < total, "part too far from its exact share");
            }

            Assert.Equal((BigInteger)minor, sum);
        }
    }

    [Fact]
    public void ParseAndFormatRoundTripForRandomAmounts()
    {
        var rng = new Random(7);
        Currency[] currencies = { Currency.BRL, Currency.JPY, Kwd, new Currency("CLF"), Currency.USD };
        for (int i = 0; i < 5000; i++)
        {
            Currency c = currencies[rng.Next(currencies.Length)];
            var amount = new Money(rng.NextInt64(long.MinValue, long.MaxValue) >> rng.Next(63), c);
            Assert.Equal(amount, Money.Parse(c, amount.ToDecimalString()));
            Assert.Equal(amount, Money.ParseLocale(c, amount.Format(MoneyLocale.PtBR), MoneyLocale.PtBR));
            Assert.Equal(amount, Money.ParseLocale(c, amount.Format(MoneyLocale.EnUS), MoneyLocale.EnUS));

            // Parsing is exact: compare against arbitrary precision.
            string text = amount.ToDecimalString();
            BigInteger scaled = BigInteger.Parse(text.Replace(".", string.Empty, StringComparison.Ordinal), System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal((BigInteger)amount.MinorUnits, scaled);
        }
    }
}
