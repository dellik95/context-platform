using Context.Domain.Enums;

namespace Context.Domain.Entities;

public class Memory
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public MemoryType Type { get; set; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public string? FactsJson { get; set; }
    // JSON array drawn from a fixed taxonomy: how-it-works, why-it-exists, what-changed,
    // problem-solution, gotcha, pattern, trade-off — lets a future agent filter memories by kind
    // of insight (e.g. "show me the gotchas") rather than only full-text search.
    public string? ConceptsJson { get; set; }
    // Per-memory file provenance (as opposed to SessionSummary.ChangedFilesJson, which is
    // session-wide) — which specific files this particular memory is actually about, so a future
    // agent working on file X can find memories scoped to it without reading the whole session.
    public string? FilesReadJson { get; set; }
    public string? FilesModifiedJson { get; set; }
    public double Confidence { get; set; } = 0.7;
    public MemoryTier Tier { get; set; } = MemoryTier.Working;
    public bool Pinned { get; set; }
    public int ReinforcementCount { get; set; }
    public Guid? SupersededById { get; set; }
    public Guid? CreatedFromSessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastReinforcedAt { get; set; }
}
