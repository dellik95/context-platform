using Context.Domain.Enums;

namespace Context.Domain.Entities;

public class MemoryRelation
{
    public Guid Id { get; set; }
    public Guid MemoryId { get; set; }
    public Guid RelatedMemoryId { get; set; }
    public MemoryRelationKind Kind { get; set; }
}
