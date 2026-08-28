using Context.Domain.Entities;

namespace Context.Graph.Abstractions;

/// <summary>
/// Write-side facade over the code graph. Depends only on <see cref="IGraphPersistence"/>
/// (defined in this project) rather than EF Core or Context.Infrastructure directly, since
/// Context.Graph sits below Context.Infrastructure in the solution's dependency graph.
/// Context.Application wires a concrete <see cref="IGraphPersistence"/> (backed by
/// Context.Infrastructure's repository) at composition time.
/// </summary>
public interface IGraphStore
{
    Task UpsertAsync(IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges, CancellationToken ct);

    Task InvalidateByOwningFileAsync(Guid projectId, Guid fileId, CancellationToken ct);
}
