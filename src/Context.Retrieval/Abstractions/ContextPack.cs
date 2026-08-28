namespace Context.Retrieval.Abstractions;

/// <summary>
/// The primary output of the retrieval engine: a token-budgeted bundle of architecture summary,
/// primary hits, historical/memory notes and open issues, ready to hand to Claude. See
/// CONTRACTS.md §6 and spec §9.7.
/// </summary>
public record ContextPack(
    string ArchitectureSummary,
    IReadOnlyList<SearchHit> PrimaryHits,
    IReadOnlyList<string> HistoricalNotes,
    IReadOnlyList<string> OpenIssues,
    int EstimatedTokens,
    bool Truncated,
    int MoreAvailable,
    string Signature);
