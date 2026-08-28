using Context.Domain.Entities;
using Context.Graph.Abstractions;

namespace Context.Graph;

/// <summary>
/// EF-Core-agnostic implementation of <see cref="IGraphStore"/>. All actual storage goes through
/// <see cref="IGraphPersistence"/> (Context.Application wires a concrete implementation backed by
/// Context.Infrastructure's repository at composition time). Every write also updates any
/// already-warm <see cref="InMemoryGraphCache"/> incrementally, scoped to the files that changed,
/// so <see cref="IGraphTraversal"/> never needs a full cache rebuild.
/// </summary>
public sealed class GraphStore : IGraphStore
{
    private readonly IGraphPersistence _persistence;
    private readonly IGraphCacheProvider _cacheProvider;

    public GraphStore(IGraphPersistence persistence, IGraphCacheProvider cacheProvider)
    {
        _persistence = persistence;
        _cacheProvider = cacheProvider;
    }

    public async Task UpsertAsync(IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges, CancellationToken ct)
    {
        var nodeList = nodes as IReadOnlyCollection<GraphNode> ?? nodes.ToList();
        var edgeList = edges as IReadOnlyCollection<GraphEdge> ?? edges.ToList();

        await _persistence.AddNodesAsync(nodeList, ct).ConfigureAwait(false);
        await _persistence.AddEdgesAsync(edgeList, ct).ConfigureAwait(false);

        var affectedProjects = nodeList.Select(n => n.ProjectId)
            .Concat(edgeList.Select(e => e.ProjectId))
            .Distinct();

        foreach (var projectId in affectedProjects)
        {
            if (!_cacheProvider.TryGetLoaded(projectId, out var cache) || cache is null)
                continue; // nothing warm for this project yet - it will hydrate fresh from persistence on first use

            var projectNodes = nodeList.Where(n => n.ProjectId == projectId).ToList();
            var projectEdges = edgeList.Where(e => e.ProjectId == projectId).ToList();

            var affectedFiles = projectNodes.Select(n => n.OwningFileId)
                .Concat(projectEdges.Select(e => e.OwningFileId))
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct();

            foreach (var fileId in affectedFiles)
                cache.InvalidateOwningFile(projectId, fileId);

            cache.Hydrate(projectNodes, projectEdges);
        }
    }

    public async Task InvalidateByOwningFileAsync(Guid projectId, Guid fileId, CancellationToken ct)
    {
        await _persistence.RemoveByOwningFileAsync(projectId, fileId, ct).ConfigureAwait(false);

        if (_cacheProvider.TryGetLoaded(projectId, out var cache) && cache is not null)
            cache.InvalidateOwningFile(projectId, fileId);
    }
}
