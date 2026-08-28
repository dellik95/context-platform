using Context.Domain.Enums;

namespace Context.Domain.Entities;

public class GraphNode
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public GraphNodeKind Kind { get; set; }
    public Guid? RefId { get; set; }                    // FileId or SymbolId, null for synthetic nodes
    public required string Label { get; set; }
    public Guid? OwningFileId { get; set; }             // drives incremental invalidation
}
