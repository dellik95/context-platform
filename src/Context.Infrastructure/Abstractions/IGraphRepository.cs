using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Infrastructure.Abstractions;

public interface IGraphRepository
{
    Task<GraphNode?> GetNodeByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<GraphNode>> GetNodesByOwningFileAsync(Guid fileId, CancellationToken ct);
    Task<IReadOnlyList<GraphEdge>> GetEdgesByNodeAsync(Guid nodeId, GraphEdgeKind[]? kinds, CancellationToken ct);

    /// <summary>All nodes of one Kind in a project -- e.g. every UnresolvedExternal placeholder, for ResolveRelationsJobHandler's resolution sweep.</summary>
    Task<IReadOnlyList<GraphNode>> GetNodesByKindAsync(Guid projectId, GraphNodeKind kind, CancellationToken ct);

    Task UpsertAsync(IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges, CancellationToken ct);

    /// <summary>
    /// Atomically replaces one file's owned nodes/edges while preserving incoming cross-file
    /// edges targeting nodes owned by <paramref name="fileId"/> that survived the update.
    /// </summary>
    Task ReplaceForFileAsync(Guid projectId, Guid fileId, IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges, CancellationToken ct);

    /// <summary>Removes every node/edge owned by the given file — the incremental-invalidation seam.</summary>
    Task InvalidateByOwningFileAsync(Guid projectId, Guid fileId, CancellationToken ct);

    /// <summary>
    /// Deletes one node outright. GraphEdge.SourceNodeId/TargetNodeId are FK-restricted against
    /// GraphNode, so a caller must first ensure no edge still references this id (e.g.
    /// ResolveRelationsJobHandler only calls this once every edge that targeted an
    /// UnresolvedExternal placeholder has been retargeted away from it).
    /// </summary>
    Task DeleteNodeAsync(Guid id, CancellationToken ct);
}
