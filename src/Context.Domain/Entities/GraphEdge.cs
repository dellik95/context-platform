using Context.Domain.Enums;

namespace Context.Domain.Entities;

public class GraphEdge
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SourceNodeId { get; set; }
    public Guid TargetNodeId { get; set; }
    public GraphEdgeKind Kind { get; set; }
    public Guid? OwningFileId { get; set; }
    public double Weight { get; set; } = 1.0;
}
