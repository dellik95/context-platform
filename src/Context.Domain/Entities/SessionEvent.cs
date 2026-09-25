using Context.Domain.Enums;

namespace Context.Domain.Entities;

public class SessionEvent
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public SessionEventType Type { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public int? TokenEstimate { get; set; }

    /// <summary>The memory-extraction batch this event was claimed into; null until claimed.</summary>
    public Guid? ExtractionBatchId { get; set; }

    /// <summary>When that batch's extraction finished; events are only deletable after this.</summary>
    public DateTimeOffset? ExtractedAt { get; set; }
}
