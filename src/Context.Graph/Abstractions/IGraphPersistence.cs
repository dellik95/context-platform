using Context.Domain.Entities;

namespace Context.Graph.Abstractions;

/// <summary>
/// Minimal storage seam that keeps Context.Graph persistence-agnostic. Context.Graph must not
/// reference EF Core or Context.Infrastructure directly (it sits below Infrastructure in the
/// dependency graph), so this interface is the only thing <see cref="IGraphStore"/> and
/// <see cref="InMemoryGraphCache"/> hydration depend on. Context.Application is expected to
/// register a concrete implementation that delegates to Context.Infrastructure's graph
/// repository at composition time.
/// </summary>
public interface IGraphPersistence
{
    Task AddNodesAsync(IEnumerable<GraphNode> nodes, CancellationToken ct);

    Task AddEdgesAsync(IEnumerable<GraphEdge> edges, CancellationToken ct);

    Task RemoveByOwningFileAsync(Guid projectId, Guid fileId, CancellationToken ct);

    Task<GraphSnapshot> GetAllAsync(Guid projectId, CancellationToken ct);
}
