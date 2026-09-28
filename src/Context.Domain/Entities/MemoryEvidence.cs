using Context.Domain.Enums;

namespace Context.Domain.Entities;

/// <summary>Append-only provenance record: why a memory exists or changed.</summary>
public class MemoryEvidence
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid MemoryId { get; set; }
    public EvidenceRelation Relation { get; set; }
    public EvidenceSource Source { get; set; }
    public Guid? SessionId { get; set; }
    public Guid? SourceMemoryId { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public string? Note { get; set; }
}
