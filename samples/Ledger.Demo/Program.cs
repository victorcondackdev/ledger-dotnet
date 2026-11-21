using System.Globalization;
using System.Text;
using Ledger.InMemory;
using Ledger.Monetary;

namespace Ledger.Demo;

/// <summary>
/// Runs a small digital-bank scenario on an in-memory ledger and prints
/// account statements, the trial balance and the audit chain head. The clock
/// and identifiers are deterministic, so the output is reproducible and
/// byte-identical to the Go demo (see expected-output.txt).
/// </summary>
public static class Program
{
    /// <summary>Entry point: <c>dotnet run --project samples/Ledger.Demo</c>.</summary>
    /// <returns>The process exit code.</returns>
    public static async Task<int> Main()
    {
        try
        {
            var output = new StringWriter(CultureInfo.InvariantCulture) { NewLine = "\n" };
            await RunAsync(output);
            Console.Out.Write(output.ToString());
            return 0;
        }
        catch (LedgerException e)
        {
            Console.Error.WriteLine("ledger-demo: " + e.Message);
            return 1;
        }
    }

    /// <summary>Runs the scenario and writes the report to <paramref name="w"/>.</summary>
    /// <param name="w">The destination.</param>
    /// <returns>A task that completes when the report is written.</returns>
    public static async Task RunAsync(TextWriter w)
    {
        var clock = new DemoClock(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));
        int n = 0;
        using var store = new InMemoryLedgerStore();
        var l = new GeneralLedger(store, new LedgerOptions
        {
            TimeProvider = clock,
            IdGenerator = () => "tx-" + (++n).ToString("000", CultureInfo.InvariantCulture),
        });

        // Each step moves the clock seven minutes forward, as in the Go demo.
        async Task<Transaction> Step(ValueTask<Transaction> operation)
        {
            try
            {
                return await operation;
            }
            finally
            {
                clock.Advance(TimeSpan.FromMinutes(7));
            }
        }

        Account[] chart =
        {
            new() { Id = "asset:bacen-reserves", Code = "1.1.01", Name = "Reserves at the central bank", Currency = Currency.BRL, Type = AccountType.Asset },
            new() { Id = "liab:wallet:alice", Code = "2.1.01", Name = "Alice", Currency = Currency.BRL, Type = AccountType.Liability, Flags = AccountFlags.NoOverdraft },
            new() { Id = "liab:wallet:bob", Code = "2.1.02", Name = "Bob", Currency = Currency.BRL, Type = AccountType.Liability, Flags = AccountFlags.NoOverdraft },
            new() { Id = "liab:merchant:padaria", Code = "2.1.10", Name = "Padaria", Currency = Currency.BRL, Type = AccountType.Liability, Flags = AccountFlags.NoOverdraft },
            new() { Id = "liab:payable:partner", Code = "2.2.01", Name = "Partner fee share", Currency = Currency.BRL, Type = AccountType.Liability },
            new() { Id = "rev:pix-fees", Code = "3.1.01", Name = "Pix fees", Currency = Currency.BRL, Type = AccountType.Revenue },
        };
        foreach (Account a in chart)
        {
            await l.OpenAccountAsync(a);
        }

        // Incoming Pix credits the wallets.
        foreach ((string to, string amount) in new[] { ("liab:wallet:alice", "1500.00"), ("liab:wallet:bob", "200.00") })
        {
            await Step(l.PostAsync(new PostRequest
            {
                IdempotencyKey = "pix-in-" + to,
                Description = "Pix received",
                Postings = new[] { Posting.Debit("asset:bacen-reserves", Brl(amount)), Posting.Credit(to, Brl(amount)) },
            }));
        }

        // Alice pays Bob R$ 320,00 with a R$ 0,99 fee shared 70/30.
        Money fee = Brl("0.99");
        Money[] split = fee.Allocate(70, 30);
        Money pixAmount = Brl("320.00");
        Money total = pixAmount + fee;
        Transaction pix = await Step(l.PostAsync(new PostRequest
        {
            IdempotencyKey = "E1234567820261102090000000000001",
            Description = "Pix to Bob",
            Metadata = new Dictionary<string, string> { ["end_to_end_id"] = "E1234567820261102090000000000001" },
            Postings = new[]
            {
                Posting.Debit("liab:wallet:alice", total),
                Posting.Credit("liab:wallet:bob", pixAmount),
                Posting.Credit("rev:pix-fees", split[0]),
                Posting.Credit("liab:payable:partner", split[1]),
            },
        }));

        // A card authorization at the bakery, captured for less.
        Transaction auth = await Step(l.HoldAsync(new PostRequest
        {
            IdempotencyKey = "auth-0001",
            Description = "Card auth Padaria",
            Postings = new[] { Posting.Debit("liab:wallet:alice", Brl("89.90")), Posting.Credit("liab:merchant:padaria", Brl("89.90")) },
            Timeout = TimeSpan.FromDays(7),
        }));
        await Step(l.PostPendingAsync(new ResolveRequest
        {
            Id = auth.Id,
            IdempotencyKey = "cap-0001",
            Description = "Card capture Padaria",
            Amount = Brl("74.35"),
        }));

        // Bob reports a scam: funds are blocked under MED for 72h, then
        // returned by reversing the original Pix.
        Transaction med = await Step(l.HoldAsync(new PostRequest
        {
            Description = "MED block",
            Postings = new[] { Posting.Debit("liab:wallet:bob", Brl("320.00")), Posting.Credit("liab:payable:partner", Brl("320.00")) },
            Timeout = TimeSpan.FromHours(72),
        }));
        await Step(l.VoidPendingAsync(new ResolveRequest { Id = med.Id, Description = "MED released for refund" }));
        await Step(l.ReverseAsync(new ResolveRequest { Id = pix.Id, IdempotencyKey = "med-refund-1", Description = "Pix refund (MED)" }));

        // Retrying a request is harmless.
        await Step(l.PostAsync(new PostRequest
        {
            IdempotencyKey = "pix-in-liab:wallet:bob",
            Description = "Pix received",
            Postings = new[] { Posting.Debit("asset:bacen-reserves", Brl("200.00")), Posting.Credit("liab:wallet:bob", Brl("200.00")) },
        }));

        foreach (string id in new[] { "liab:wallet:alice", "liab:wallet:bob" })
        {
            await PrintStatementAsync(w, l, id);
        }

        await PrintTrialBalanceAsync(w, l);
        ChainHead head = await l.VerifyChainAsync();
        await l.VerifyBalancesAsync();
        w.Write("\naudit chain verified: " + head.Seq.ToString(CultureInfo.InvariantCulture) + " transactions, head " + head.Hash.ToString().Substring(0, 16) + "\n");
    }

    private static Money Brl(string amount) => Money.Parse(Currency.BRL, amount);

    private static async Task PrintStatementAsync(TextWriter w, GeneralLedger l, string id)
    {
        Statement st = await l.GetStatementAsync(id, new StatementOptions { IncludeHolds = true });
        w.Write("\nStatement " + st.Account.Name + " (" + id + ")\n");
        var table = new List<string[]> { new[] { "time", "seq", "description", "phase", "amount", "balance", "available" } };
        foreach (StatementLine ln in st.Lines)
        {
            table.Add(new[]
            {
                ln.Time.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture),
                ln.Seq.ToString(CultureInfo.InvariantCulture),
                ln.Description,
                ln.Phase.ToCode(),
                ln.Amount.Format(MoneyLocale.PtBR),
                ln.Balance.Format(MoneyLocale.PtBR),
                ln.Available.Format(MoneyLocale.PtBR),
            });
        }

        w.Write(AlignRight(table));
    }

    private static async Task PrintTrialBalanceAsync(TextWriter w, GeneralLedger l)
    {
        TrialBalance tb = await l.GetTrialBalanceAsync();
        w.Write("\nTrial balance\n");
        var table = new List<string[]> { new[] { "code", "account", "debits", "credits", "balance" } };
        foreach (TrialBalanceRow r in tb.Rows)
        {
            table.Add(new[]
            {
                r.Account.Code,
                r.Account.Id,
                r.DebitsPosted.ToDecimalString(),
                r.CreditsPosted.ToDecimalString(),
                r.Balance.ToDecimalString(),
            });
        }

        CurrencyTotals t = tb.Totals[Currency.BRL];
        table.Add(new[]
        {
            string.Empty,
            "total",
            new Money(t.DebitsPosted, Currency.BRL).ToDecimalString(),
            new Money(t.CreditsPosted, Currency.BRL).ToDecimalString(),
            string.Empty,
        });
        w.Write(AlignRight(table));
        string status = "balanced";
        try
        {
            tb.Check();
        }
        catch (LedgerException e)
        {
            status = e.Message;
        }

        w.Write(new string('-', 20) + " " + status + "\n");
    }

    /// <summary>Right-aligns every cell in its column with two spaces of padding, like Go's text/tabwriter with AlignRight.</summary>
    private static string AlignRight(List<string[]> rows)
    {
        int columns = rows[0].Length;
        var widths = new int[columns];
        foreach (string[] row in rows)
        {
            for (int c = 0; c < columns; c++)
            {
                widths[c] = Math.Max(widths[c], row[c].Length + 2);
            }
        }

        var b = new StringBuilder();
        foreach (string[] row in rows)
        {
            for (int c = 0; c < columns; c++)
            {
                b.Append(' ', widths[c] - row[c].Length).Append(row[c]);
            }

            b.Append('\n');
        }

        return b.ToString();
    }

    private sealed class DemoClock : TimeProvider
    {
        private DateTimeOffset _now;

        public DemoClock(DateTimeOffset start)
        {
            _now = start;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
