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

    /// <summary>
    /// Same hop-expansion as <see cref="NeighborsAsync"/>, but also returns the origin node and
    /// every edge whose endpoints are both within the resulting node set - what a graph
    /// visualization needs that a flat node list can't express.
    /// </summary>
    Task<GraphNeighborhood> NeighborhoodAsync(
        Guid nodeId, GraphEdgeKind[]? kinds, int hops, CancellationToken ct);

    Task<IReadOnlyList<GraphNode>?> ShortestPathAsync(
        Guid fromId, Guid toId, CancellationToken ct);

    Task<ImpactSet> BlastRadiusAsync(Guid symbolNodeId, int maxHops, CancellationToken ct);
}
