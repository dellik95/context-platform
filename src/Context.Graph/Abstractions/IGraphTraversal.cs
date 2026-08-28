using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Graph.Abstractions;

/// <summary>
/// Read-side graph queries over the warm <see cref="Context.Graph.InMemoryGraphCache"/>.
/// </summary>
public interface IGraphTraversal
{
    Task<IReadOnlyList<GraphNode>> NeighborsAsync(
        Guid nodeId, GraphEdgeKind[]? kinds, int hops, CancellationToken ct);

    Task<IReadOnlyList<GraphNode>?> ShortestPathAsync(
        Guid fromId, Guid toId, CancellationToken ct);

    Task<ImpactSet> BlastRadiusAsync(Guid symbolNodeId, int maxHops, CancellationToken ct);
}
