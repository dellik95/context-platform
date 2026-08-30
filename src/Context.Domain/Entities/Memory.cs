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
    public double Confidence { get; set; } = 0.7;
    public MemoryTier Tier { get; set; } = MemoryTier.Working;
    public bool Pinned { get; set; }
    public int ReinforcementCount { get; set; }
    public Guid? SupersededById { get; set; }
    public Guid? CreatedFromSessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastReinforcedAt { get; set; }
}
