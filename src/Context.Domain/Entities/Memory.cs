using Context.Domain.Enums;

namespace Context.Domain.Entities;

public class Memory
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public MemoryType Type { get; set; }
    public MemoryStatus Status { get; set; } = MemoryStatus.Active;
    public required string Title { get; set; }
    public required string Body { get; set; }
    // Short reusable subsystem phrase shared by memories about the same subject across sessions.
    public string? Topic { get; set; }
    // Normalized Topic used for exact-match grouping; Topic keeps the display form.
    public string? TopicKey { get; set; }
    // Repo-relative directory the memory is scoped to, for "everything under this component".
    public string? ScopePath { get; set; }
    public string? FactsJson { get; set; }
    public string? ConceptsJson { get; set; }
    public string? FilesReadJson { get; set; }
    public string? FilesModifiedJson { get; set; }
    public double Confidence { get; set; } = 0.7;
    public MemoryTier Tier { get; set; } = MemoryTier.Working;
    public bool Pinned { get; set; }
    public int ReinforcementCount { get; set; }
    // Number of independent evidence sources backing this memory.
    public int SourceCount { get; set; } = 1;
    public Guid? SupersededById { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
    public Guid? CreatedFromSessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastReinforcedAt { get; set; }

    public bool IsLive => Status is MemoryStatus.Active or MemoryStatus.Contested;
}
