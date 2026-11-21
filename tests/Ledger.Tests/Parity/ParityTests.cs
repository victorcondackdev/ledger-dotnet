using System.Globalization;
using System.Text.RegularExpressions;
using Ledger.InMemory;
using Ledger.Monetary;

namespace Ledger.Tests.Parity;

/// <summary>
/// Cross-language parity: every vector in <see cref="GoVectors"/> was produced
/// by running the Go implementation (github.com/victorcondackdev/ledger-go).
/// The money tables must match exactly, and replaying each ledger scenario
/// step by step must reproduce every outcome, every transaction hash, every
/// statement page and cursor, the trial balance, point-in-time balances and
/// finally the same audit chain head.
/// </summary>
public class ParityTests
{
    private static readonly Regex Numbered = new(@"^[A-Za-z]+\((\d+)\)$", RegexOptions.CultureInvariant);

    private static void Check(List<string> failures, string what, string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            failures.Add(what + ": expected " + expected + ", got " + actual);
        }
    }

    private static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void CurrencyTableMatchesGo()
    {
        var failures = new List<string>();
        foreach (var v in GoVectors.Currencies)
        {
            string parsed = Currency.TryParse(v.Code, out Currency c) ? c.Code : string.Empty;
            string error = parsed.Length == 0 ? "UnknownCurrency" : string.Empty;
            var raw = new Currency(v.Code);
            Check(failures, "'" + v.Code + "' parse", v.Parsed + "|" + v.Error, parsed + "|" + error);
            Check(
                failures,
                "'" + v.Code + "' table",
                N(v.Exponent) + "|" + N(v.Numeric) + "|" + v.Symbol + "|" + v.Valid,
                N(raw.Exponent) + "|" + N(raw.NumericCode) + "|" + raw.Symbol + "|" + raw.IsValid);
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void ParseMatchesGo()
    {
        var failures = new List<string>();
        foreach (var v in GoVectors.Parse)
        {
            string actual;
            try
            {
                actual = N(Money.Parse(new Currency(v.Currency), v.Input).MinorUnits) + "|";
            }
            catch (MoneyException e)
            {
                actual = "0|" + e.Code;
            }

            Check(failures, v.Currency + " '" + v.Input + "'", N(v.Minor) + "|" + v.Error, actual);
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void FormatMatchesGo()
    {
        var failures = new List<string>();
        foreach (var v in GoVectors.Format)
        {
            var amount = new Money(v.Minor, new Currency(v.Currency));
            string what = N(v.Minor) + " " + v.Currency;
            Check(failures, what + " decimal", v.Decimal, amount.ToDecimalString());
            Check(failures, what + " pt-BR", v.PtBR, amount.Format(MoneyLocale.PtBR));
            Check(failures, what + " en-US", v.EnUS, amount.Format(MoneyLocale.EnUS));
            Check(failures, what + " text", v.Text, amount.ToString());
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void ParseLocaleMatchesGo()
    {
        var failures = new List<string>();
        foreach (var v in GoVectors.ParseLocale)
        {
            MoneyLocale locale = v.Locale == "en-US" ? MoneyLocale.EnUS : MoneyLocale.PtBR;
            string actual;
            try
            {
                actual = N(Money.ParseLocale(new Currency(v.Currency), v.Input, locale).MinorUnits) + "|";
            }
            catch (MoneyException e)
            {
                actual = "0|" + e.Code;
            }

            Check(failures, v.Currency + " " + v.Locale + " '" + v.Input + "'", N(v.Minor) + "|" + v.Error, actual);
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void AllocateMatchesGo()
    {
        var failures = new List<string>();
        foreach (var v in GoVectors.Allocate)
        {
            string actual;
            try
            {
                actual = string.Join(",", new Money(v.Minor, Currency.BRL).Allocate(v.Ratios).Select(p => N(p.MinorUnits))) + "|";
            }
            catch (MoneyException e)
            {
                actual = "|" + e.Code;
            }

            Check(failures, N(v.Minor) + " by " + string.Join(",", v.Ratios.Select(N)), string.Join(",", v.Parts.Select(N)) + "|" + v.Error, actual);
        }

        Assert.Empty(failures);
    }

    [Fact]
    public Task ScriptedScenarioMatchesGo() => ReplayAsync(GoVectors.Scenarios[0]);

    [Fact]
    public Task RandomScenarioMatchesGo() => ReplayAsync(GoVectors.Scenarios[1]);

    private static async Task ReplayAsync(ScenarioVectors s)
    {
        var clock = new ManualClock(DateTimeOffset.FromUnixTimeSeconds(s.StartUnix));
        var ids = new SequentialIds();
        var l = new GeneralLedger(new InMemoryLedgerStore(), new LedgerOptions { TimeProvider = clock, IdGenerator = ids.Next });

        // Every step must have the outcome Go observed, including which
        // transaction IDs and hashes it produced and which error it raised.
        var failures = new List<string>();
        for (int i = 0; i < s.Ops.Length && failures.Count < 10; i++)
        {
            ScenarioOp op = s.Ops[i];
            clock.Advance(TimeSpan.FromSeconds(op.AdvanceSeconds));
            string actual = await ExecuteAsync(l, op);
            Check(failures, s.Name + " step " + N(i) + " (" + op.Op + ")", op.Expect, actual);
        }

        Assert.Empty(failures);

        // The sealed transactions are byte-identical: same canonical encoding,
        // same request fingerprints, same chain.
        var transactions = new List<ScenarioTx>();
        for (long after = 0; ;)
        {
            IReadOnlyList<Transaction> page = await l.GetTransactionsAsync(after, 1000);
            foreach (Transaction t in page)
            {
                transactions.Add(new ScenarioTx(
                    t.Seq,
                    t.Id,
                    t.Kind.ToCode(),
                    t.Ref ?? string.Empty,
                    t.IdempotencyKey ?? string.Empty,
                    t.Timestamp.ToUnixTimeSeconds(),
                    t.ExpiresAt?.ToUnixTimeSeconds() ?? 0,
                    t.Description,
                    string.Join("; ", t.Postings.Select(p => p.Direction.ToCode() + " " + p.Account + " " + p.Amount.ToString())),
                    t.RequestHash.ToString(),
                    t.PrevHash.ToString(),
                    t.Hash.ToString()));
                after = t.Seq;
            }

            if (page.Count < 1000)
            {
                break;
            }
        }

        Assert.Equal(s.Transactions, transactions);

        // Statements, page by page, with the same opaque cursors.
        var lines = new List<ScenarioLine>();
        var pages = new List<ScenarioPage>();
        IReadOnlyList<Account> accounts = await l.GetAccountsAsync();
        foreach (Account a in accounts)
        {
            foreach (bool holds in new[] { true, false })
            {
                var options = new StatementOptions { Limit = holds ? 4 : 3, IncludeHolds = holds };
                for (int pageNo = 1; ; pageNo++)
                {
                    Statement st = await l.GetStatementAsync(a.Id, options);
                    foreach (StatementLine ln in st.Lines)
                    {
                        lines.Add(new ScenarioLine(
                            a.Id,
                            holds,
                            pageNo,
                            ln.Seq,
                            ln.TransactionId,
                            ln.Kind.ToCode(),
                            ln.Time.ToUnixTimeSeconds(),
                            ln.Description,
                            ln.Direction.ToCode(),
                            ln.Phase.ToCode(),
                            ln.Amount.MinorUnits,
                            ln.Balance.MinorUnits,
                            ln.Available.MinorUnits));
                    }

                    pages.Add(new ScenarioPage(a.Id, holds, pageNo, st.Lines.Count, st.NextCursor ?? string.Empty));
                    if (st.NextCursor is null)
                    {
                        break;
                    }

                    options = options with { Cursor = st.NextCursor };
                }
            }
        }

        Assert.Equal(s.Lines, lines);
        Assert.Equal(s.Pages, pages);

        TrialBalance tb = await l.GetTrialBalanceAsync();
        tb.Check();
        Assert.Equal(
            s.TrialBalance,
            tb.Rows.Select(r => new ScenarioTrialRow(
                r.Account.Id,
                r.Account.Currency.Code,
                r.DebitsPosted.MinorUnits,
                r.CreditsPosted.MinorUnits,
                r.DebitsPending.MinorUnits,
                r.CreditsPending.MinorUnits,
                r.Balance.MinorUnits)));

        var balancesAt = new List<ScenarioBalanceAt>();
        foreach (ScenarioBalanceAt probe in s.BalancesAt)
        {
            Balance b = await l.GetBalanceAtAsync(probe.Account, DateTimeOffset.FromUnixTimeSeconds(probe.At));
            balancesAt.Add(new ScenarioBalanceAt(probe.Account, probe.At, b.Posted.MinorUnits, b.Pending.MinorUnits, b.Available.MinorUnits));
        }

        Assert.Equal(s.BalancesAt, balancesAt);

        // The headline: the same history yields the same chain head in both languages.
        ChainHead head = await l.VerifyChainAsync();
        await l.VerifyBalancesAsync();
        Assert.Equal(s.HeadSeq, head.Seq);
        Assert.Equal(s.HeadHash, head.Hash.ToString());
    }

    private static async Task<string> ExecuteAsync(GeneralLedger l, ScenarioOp op)
    {
        try
        {
            switch (op.Op)
            {
                case "open":
                    await l.OpenAccountAsync(ToAccount(op.Open ?? throw new InvalidOperationException("open without account")));
                    return "ok";
                case "post":
                    return Describe(await l.PostAsync(ToPostRequest(op)));
                case "hold":
                    return Describe(await l.HoldAsync(ToPostRequest(op)));
                case "post_pending":
                    return Describe(await l.PostPendingAsync(ToResolveRequest(op)));
                case "void_pending":
                    return Describe(await l.VoidPendingAsync(ToResolveRequest(op)));
                case "reverse":
                    return Describe(await l.ReverseAsync(ToResolveRequest(op)));
                case "expire":
                    return "expired " + N(await l.ExpirePendingAsync());
                case "freeze":
                    await l.FreezeAsync(op.Account);
                    return "ok";
                case "unfreeze":
                    await l.UnfreezeAsync(op.Account);
                    return "ok";
                default:
                    throw new InvalidOperationException("unknown step " + op.Op);
            }
        }
        catch (LedgerException e)
        {
            return "error " + e.Code;
        }
    }

    private static string Describe(Transaction t) => t.Id + " seq=" + N(t.Seq) + " " + t.Hash;

    private static IReadOnlyDictionary<string, string>? ToMetadata(string[] pairs)
    {
        if (pairs.Length == 0)
        {
            return null;
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i + 1 < pairs.Length; i += 2)
        {
            metadata[pairs[i]] = pairs[i + 1];
        }

        return metadata;
    }

    private static PostRequest ToPostRequest(ScenarioOp op) => new()
    {
        IdempotencyKey = op.Key.Length == 0 ? null : op.Key,
        Description = op.Description,
        Metadata = ToMetadata(op.Metadata),
        Postings = op.Postings.Select(p => new Posting(p.Account, ParseDirection(p.Direction), new Money(p.Minor, new Currency(p.Currency)))).ToArray(),
        Timeout = TimeSpan.FromSeconds(op.TimeoutSeconds),
    };

    private static ResolveRequest ToResolveRequest(ScenarioOp op) => new()
    {
        Id = op.Ref,
        IdempotencyKey = op.Key.Length == 0 ? null : op.Key,
        Description = op.Description,
        Metadata = ToMetadata(op.Metadata),
        Amount = op.AmountCurrency.Length == 0 ? null : new Money(op.Amount, new Currency(op.AmountCurrency)),
    };

    private static Account ToAccount(ScenarioAccount a) => new()
    {
        Id = a.Id,
        Code = a.Code,
        Name = a.Name,
        Currency = new Currency(a.Currency),
        Type = a.Type switch
        {
            "asset" => AccountType.Asset,
            "liability" => AccountType.Liability,
            "equity" => AccountType.Equity,
            "revenue" => AccountType.Revenue,
            "expense" => AccountType.Expense,
            _ => (AccountType)ParseNumbered(a.Type),
        },
        Normal = a.Normal.Length == 0 ? default : ParseDirection(a.Normal),
        Flags = a.Flags.Split('|').Aggregate(AccountFlags.None, (flags, name) => flags | ParseFlag(name)),
    };

    private static AccountFlags ParseFlag(string name) => name switch
    {
        "none" => AccountFlags.None,
        "no_overdraft" => AccountFlags.NoOverdraft,
        "frozen" => AccountFlags.Frozen,
        _ => throw new InvalidOperationException("unknown flag " + name),
    };

    private static Direction ParseDirection(string name) => name switch
    {
        "debit" => Direction.Debit,
        "credit" => Direction.Credit,
        _ => (Direction)ParseNumbered(name),
    };

    private static byte ParseNumbered(string name)
    {
        Match m = Numbered.Match(name);
        if (!m.Success)
        {
            throw new InvalidOperationException("unexpected name " + name);
        }

        return byte.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
