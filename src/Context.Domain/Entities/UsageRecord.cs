namespace Context.Domain.Entities;

/// <summary>One LLM call's token usage, recorded for cost accounting (BACKLOG-27).</summary>
public class UsageRecord
{
    public Guid Id { get; set; }                 // PK
    public Guid ProjectId { get; set; }
    public Guid? SessionId { get; set; }
    public string Purpose { get; set; } = string.Empty;   // e.g. "SessionSummary", "MemoryExtraction", "MemoryConsistencyCheck"
    public string Provider { get; set; } = string.Empty;
    public string? Model { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
