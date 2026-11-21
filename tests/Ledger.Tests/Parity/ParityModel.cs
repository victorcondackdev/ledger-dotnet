namespace Ledger.Tests.Parity;

// Shapes of the vectors that tools/vecgen emits from the Go implementation.

/// <summary>An account definition for an <c>open</c> step. Type, normal balance and flags use the Go names.</summary>
internal sealed record ScenarioAccount(string Id, string Code, string Name, string Currency, string Type, string Normal, string Flags);

/// <summary>One leg of a request.</summary>
internal sealed record ScenarioPosting(string Account, string Direction, long Minor, string Currency);

/// <summary>
/// One scenario step and the outcome observed in Go: <c>"tx-005 seq=4 &lt;hash&gt;"</c>,
/// <c>"error InsufficientFunds"</c>, <c>"expired 2"</c> or <c>"ok"</c>.
/// </summary>
internal sealed record ScenarioOp(
    string Op,
    long AdvanceSeconds,
    ScenarioAccount? Open,
    string Account,
    string Key,
    string Description,
    string[] Metadata,
    ScenarioPosting[] Postings,
    long TimeoutSeconds,
    string Ref,
    long Amount,
    string AmountCurrency,
    string Expect);

/// <summary>A sealed transaction as stored after the scenario.</summary>
internal sealed record ScenarioTx(
    long Seq,
    string Id,
    string Kind,
    string Ref,
    string Key,
    long Timestamp,
    long ExpiresAt,
    string Description,
    string Postings,
    string RequestHash,
    string PrevHash,
    string Hash);

/// <summary>One statement line; <c>Holds</c> tells which statement variant it belongs to.</summary>
internal sealed record ScenarioLine(
    string Account,
    bool Holds,
    int Page,
    long Seq,
    string TxId,
    string Kind,
    long Time,
    string Description,
    string Direction,
    string Phase,
    long Amount,
    long Balance,
    long Available);

/// <summary>One statement page and the cursor that follows it (empty on the last page).</summary>
internal sealed record ScenarioPage(string Account, bool Holds, int Page, int Lines, string NextCursor);

/// <summary>One trial balance row.</summary>
internal sealed record ScenarioTrialRow(string Account, string Currency, long DebitsPosted, long CreditsPosted, long DebitsPending, long CreditsPending, long Balance);

/// <summary>A point-in-time balance probe.</summary>
internal sealed record ScenarioBalanceAt(string Account, long At, long Posted, long Pending, long Available);

/// <summary>A complete scenario: the steps, and everything the ledger holds afterwards.</summary>
internal sealed record ScenarioVectors(
    string Name,
    long StartUnix,
    ScenarioOp[] Ops,
    ScenarioTx[] Transactions,
    ScenarioLine[] Lines,
    ScenarioPage[] Pages,
    ScenarioTrialRow[] TrialBalance,
    ScenarioBalanceAt[] BalancesAt,
    long HeadSeq,
    string HeadHash);
