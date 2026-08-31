using Context.Domain.Enums;

namespace Context.Graph.Abstractions;

/// <summary>
/// Serializable projection of <see cref="Context.Domain.Entities.GraphEdge"/> for
/// <see cref="GraphNeighborhood"/> - drops the Id/OwningFileId/Weight fields a graph-rendering
/// caller has no use for.
/// </summary>
public sealed record GraphEdgeDto(Guid SourceId, Guid TargetId, GraphEdgeKind Kind);
