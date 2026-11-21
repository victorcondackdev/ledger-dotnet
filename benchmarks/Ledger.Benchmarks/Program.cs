using System.Globalization;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Ledger.InMemory;
using Ledger.Monetary;

namespace Ledger.Benchmarks;

/// <summary>Entry point: <c>dotnet run -c Release --project benchmarks/Ledger.Benchmarks -- --filter '*'</c>.</summary>
public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

/// <summary>Write-path benchmarks on the in-memory store; every transaction is validated, fingerprinted, sealed with SHA-256, appended to the chain and indexed.</summary>
[MemoryDiagnoser]
public class PostBenchmarks
{
    private const int Workers = 8;
    private const int PostsPerWorker = 256;

    private static readonly Money One = new(1, Currency.BRL);
    private static readonly IReadOnlyDictionary<string, string> Metadata = new Dictionary<string, string> { ["end_to_end_id"] = "E0000" };

    private InMemoryLedgerStore _store = null!;
    private GeneralLedger _ledger = null!;
    private PostRequest _transfer = null!;
    private PostRequest[] _hot = null!;
    private int _key;

    [GlobalSetup]
    public void Setup() => SetupAsync().GetAwaiter().GetResult();

    [GlobalCleanup]
    public void Cleanup() => _store.Dispose();

    /// <summary>One writer posting two-leg transfers.</summary>
    [Benchmark]
    public async Task<long> Post() => (await _ledger.PostAsync(_transfer)).Seq;

    /// <summary>Adds an idempotency key and metadata, as a Pix handler would.</summary>
    [Benchmark]
    public async Task<long> PostIdempotent()
    {
        int k = Interlocked.Increment(ref _key);
        Transaction t = await _ledger.PostAsync(_transfer with
        {
            IdempotencyKey = "E" + k.ToString("D31", CultureInfo.InvariantCulture),
            Metadata = Metadata,
        });
        return t.Seq;
    }

    /// <summary>Parallel writers on disjoint wallet pairs that all also credit one fee account: the classic hot row, where every writer queues on its lock.</summary>
    [Benchmark(OperationsPerInvoke = Workers * PostsPerWorker)]
    public async Task PostParallelHotAccount()
    {
        var tasks = new Task[Workers];
        for (int w = 0; w < Workers; w++)
        {
            PostRequest request = _hot[w];
            tasks[w] = Task.Run(async () =>
            {
                for (int i = 0; i < PostsPerWorker; i++)
                {
                    await _ledger.PostAsync(request);
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    private static string Wallet(int i) => "wallet:" + i.ToString("0000", CultureInfo.InvariantCulture);

    private async Task SetupAsync()
    {
        _store = new InMemoryLedgerStore();
        _ledger = new GeneralLedger(_store);
        await _ledger.OpenAccountAsync(new Account { Id = "asset:settlement", Currency = Currency.BRL, Type = AccountType.Asset });
        await _ledger.OpenAccountAsync(new Account { Id = "revenue:fees", Currency = Currency.BRL, Type = AccountType.Revenue });
        var funding = new Money(1L << 50, Currency.BRL);
        for (int i = 0; i < 2 * Workers; i++)
        {
            await _ledger.OpenAccountAsync(new Account { Id = Wallet(i), Currency = Currency.BRL, Type = AccountType.Liability, Flags = AccountFlags.NoOverdraft });
            await _ledger.PostAsync(new PostRequest { Postings = new[] { Posting.Debit("asset:settlement", funding), Posting.Credit(Wallet(i), funding) } });
        }

        _transfer = new PostRequest { Postings = new[] { Posting.Debit(Wallet(0), One), Posting.Credit(Wallet(1), One) } };
        var amount = new Money(100, Currency.BRL);
        _hot = new PostRequest[Workers];
        for (int w = 0; w < Workers; w++)
        {
            _hot[w] = new PostRequest
            {
                Postings = new[]
                {
                    Posting.Debit(Wallet(2 * w), amount + One),
                    Posting.Credit(Wallet((2 * w) + 1), amount),
                    Posting.Credit("revenue:fees", One),
                },
            };
        }
    }
}
