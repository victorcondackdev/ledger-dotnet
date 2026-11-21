# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project
adheres to [Semantic Versioning](https://semver.org/).

## [v0.1.0] - 2026-11-21

First public release: a .NET 8 port of
[ledger-go](https://github.com/victorcondackdev/ledger-go) v0.1.0.

### Added

- `Ledger.Monetary`: `Money` as `long` minor units plus an ISO 4217
  `Currency` with an exponent table (BRL, USD, EUR, JPY, KWD, CLF, ...);
  overflow-checked `Add`, `Subtract`, `Negate`, `Abs`, `Multiply`,
  `CompareTo` and operators; `Allocate` (largest remainder, `UInt128`
  intermediates) and `Split`; `Parse`/`ToDecimalString` ("1234.56") and
  `ParseLocale`/`Format` ("R$ 1.234,56") with `PtBR` and `EnUS` locales,
  implemented without `CultureInfo`.
- `GeneralLedger`: accounts with type, normal balance and `NoOverdraft` /
  `Frozen` flags; balanced multi-leg, multi-currency transactions;
  idempotency keys with replay and conflict detection; two-phase transfers
  (`HoldAsync`, `PostPendingAsync` with proportional partial post,
  `VoidPendingAsync`, `ExpirePendingAsync`); `ReverseAsync` with
  compensating, linked transactions.
- Queries: `GetBalanceAsync`, `GetBalanceAtAsync` (point in time),
  `GetStatementAsync` with running balances and cursor pagination,
  `GetTrialBalanceAsync` with `Check`, `GetTransactionsAsync` for tailing.
- Hash-chained audit log (SHA-256 over a canonical encoding) with
  `VerifyChainAsync`, plus the `VerifyBalancesAsync` replay audit. The
  encoding is byte-identical to ledger-go.
- `LedgerOptions`: `TimeProvider` clock, id generator (UUIDv7 by default) and
  `OnCommit` hooks.
- `ILedgerStore` async contract and `InMemoryLedgerStore`, with ordered
  per-account `SemaphoreSlim` locking and immutable snapshot reads.
- Cross-language parity tests from Go-generated vectors (money tables and two
  full ledger scenarios down to the chain head), a port of the Go test suite,
  tamper tests and a concurrent simulation.
- `samples/Ledger.Demo` (output identical to the Go demo) and BenchmarkDotNet
  benchmarks.

[v0.1.0]: https://github.com/victorcondackdev/ledger-dotnet/releases/tag/v0.1.0
