using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Infrastructure.Abstractions;

public interface IGraphRepository
{
    Task<GraphNode?> GetNodeByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<GraphNode>> GetNodesByOwningFileAsync(Guid fileId, CancellationToken ct);
    Task<IReadOnlyList<GraphEdge>> GetEdgesByNodeAsync(Guid nodeId, GraphEdgeKind[]? kinds, CancellationToken ct);

    Task UpsertAsync(IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges, CancellationToken ct);

    /// <summary>Removes every node/edge owned by the given file — the incremental-invalidation seam.</summary>
    Task InvalidateByOwningFileAsync(Guid projectId, Guid fileId, CancellationToken ct);
}
