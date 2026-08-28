using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Graph.Abstractions;

namespace Context.Graph;

/// <summary>
/// BFS-based <see cref="IGraphTraversal"/> over the warm <see cref="InMemoryGraphCache"/>.
/// None of the IGraphTraversal contract methods carry a ProjectId, so a node's owning cache is
/// resolved by scanning already-loaded caches (<see cref="IGraphCacheProvider.FindCacheContaining"/>);
/// see the implementation report for the resulting limitation on cold, never-touched projects.
/// </summary>
public sealed class GraphTraversal : IGraphTraversal
{
    private static readonly GraphEdgeKind[] CallerEdgeKinds =
        [GraphEdgeKind.Calls, GraphEdgeKind.References, GraphEdgeKind.Implements, GraphEdgeKind.Extends];

    private static readonly GraphEdgeKind[] ImplementerEdgeKinds =
        [GraphEdgeKind.Implements, GraphEdgeKind.Extends];

    private readonly IGraphCacheProvider _cacheProvider;

    public GraphTraversal(IGraphCacheProvider cacheProvider)
    {
        _cacheProvider = cacheProvider;
    }

    public Task<IReadOnlyList<GraphNode>> NeighborsAsync(
        Guid nodeId, GraphEdgeKind[]? kinds, int hops, CancellationToken ct)
    {
        var cache = _cacheProvider.FindCacheContaining(nodeId);
        if (cache is null || hops <= 0)
            return Task.FromResult<IReadOnlyList<GraphNode>>(Array.Empty<GraphNode>());

        var kindFilter = kinds is { Length: > 0 } ? new HashSet<GraphEdgeKind>(kinds) : null;
        var visited = new HashSet<Guid> { nodeId };
        var frontier = new List<Guid> { nodeId };
        var result = new List<GraphNode>();

        for (var hop = 0; hop < hops && frontier.Count > 0; hop++)
        {
            ct.ThrowIfCancellationRequested();
            var next = new List<Guid>();

            foreach (var currentId in frontier)
            {
                foreach (var edge in EnumerateBothDirections(cache, currentId))
                {
                    if (kindFilter is not null && !kindFilter.Contains(edge.Kind))
                        continue;

                    var neighborId = edge.SourceNodeId == currentId ? edge.TargetNodeId : edge.SourceNodeId;
                    if (!visited.Add(neighborId))
                        continue;

                    var neighborNode = cache.GetNode(neighborId);
                    if (neighborNode is null)
                        continue;

                    result.Add(neighborNode);
                    next.Add(neighborId);
                }
            }

            frontier = next;
        }

        return Task.FromResult<IReadOnlyList<GraphNode>>(result);
    }

    public Task<IReadOnlyList<GraphNode>?> ShortestPathAsync(Guid fromId, Guid toId, CancellationToken ct)
    {
        var cache = _cacheProvider.FindCacheContaining(fromId);
        if (cache is null || cache.GetNode(toId) is null)
            return Task.FromResult<IReadOnlyList<GraphNode>?>(null);

        if (fromId == toId)
        {
            var self = cache.GetNode(fromId);
            return Task.FromResult<IReadOnlyList<GraphNode>?>(
                self is null ? null : new List<GraphNode> { self });
        }

        var parents = new Dictionary<Guid, Guid> { [fromId] = fromId };
        var queue = new Queue<Guid>();
        queue.Enqueue(fromId);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var currentId = queue.Dequeue();
            if (currentId == toId)
                break;

            foreach (var edge in EnumerateBothDirections(cache, currentId))
            {
                var neighborId = edge.SourceNodeId == currentId ? edge.TargetNodeId : edge.SourceNodeId;
                if (parents.ContainsKey(neighborId))
                    continue;

                parents[neighborId] = currentId;
                queue.Enqueue(neighborId);
            }
        }

        if (!parents.ContainsKey(toId))
            return Task.FromResult<IReadOnlyList<GraphNode>?>(null);

        var pathIds = new LinkedList<Guid>();
        var walk = toId;
        while (true)
        {
            pathIds.AddFirst(walk);
            if (walk == fromId)
                break;
            walk = parents[walk];
        }

        var pathNodes = pathIds
            .Select(id => cache.GetNode(id))
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();

        return Task.FromResult<IReadOnlyList<GraphNode>?>(pathNodes);
    }

    public Task<ImpactSet> BlastRadiusAsync(Guid symbolNodeId, int maxHops, CancellationToken ct)
    {
        var cache = _cacheProvider.FindCacheContaining(symbolNodeId);
        if (cache is null)
        {
            return Task.FromResult(new ImpactSet(
                Array.Empty<ImpactNode>(), Array.Empty<ImpactNode>(), Array.Empty<ImpactNode>()));
        }

        // Who depends on this symbol: walk Calls/References/Implements/Extends edges backwards
        // (target -> source) up to maxHops.
        var callers = ReverseWalk(cache, symbolNodeId, CallerEdgeKinds, maxHops, ct);

        // Subtypes/implementations specifically: same walk restricted to Implements/Extends.
        var implementers = ReverseWalk(cache, symbolNodeId, ImplementerEdgeKinds, maxHops, ct);

        // Tests that cover the symbol itself or any of its (transitive) callers.
        var coverageTargets = new HashSet<Guid> { symbolNodeId };
        foreach (var caller in callers)
            coverageTargets.Add(caller.Node.Id);

        var coveringTests = new List<ImpactNode>();
        var seenTests = new HashSet<Guid>();

        foreach (var targetId in coverageTargets)
        {
            ct.ThrowIfCancellationRequested();
            var targetNode = cache.GetNode(targetId);

            foreach (var edge in cache.GetIncoming(targetId))
            {
                if (edge.Kind != GraphEdgeKind.Tests || !seenTests.Add(edge.SourceNodeId))
                    continue;

                var testNode = cache.GetNode(edge.SourceNodeId);
                if (testNode is null)
                    continue;

                var label = targetNode?.Label ?? targetId.ToString();
                coveringTests.Add(new ImpactNode(testNode, 1, $"{testNode.Label} -Tests-> {label}"));
            }
        }

        return Task.FromResult(new ImpactSet(callers, implementers, coveringTests));
    }

    /// <summary>Walks incoming edges of the given kinds backwards (target -> source), BFS up to maxHops.</summary>
    private static IReadOnlyList<ImpactNode> ReverseWalk(
        InMemoryGraphCache cache, Guid startId, IReadOnlyCollection<GraphEdgeKind> kinds, int maxHops, CancellationToken ct)
    {
        var results = new List<ImpactNode>();
        var visited = new HashSet<Guid> { startId };
        var pathById = new Dictionary<Guid, string> { [startId] = cache.GetNode(startId)?.Label ?? startId.ToString() };
        var frontier = new List<Guid> { startId };

        for (var hop = 1; hop <= Math.Max(0, maxHops) && frontier.Count > 0; hop++)
        {
            ct.ThrowIfCancellationRequested();
            var next = new List<Guid>();

            foreach (var currentId in frontier)
            {
                foreach (var edge in cache.GetIncoming(currentId))
                {
                    if (!kinds.Contains(edge.Kind))
                        continue;

                    var sourceId = edge.SourceNodeId;
                    if (!visited.Add(sourceId))
                        continue;

                    var sourceNode = cache.GetNode(sourceId);
                    if (sourceNode is null)
                        continue;

                    var path = $"{sourceNode.Label} -{edge.Kind}-> {pathById[currentId]}";
                    pathById[sourceId] = path;
                    results.Add(new ImpactNode(sourceNode, hop, path));
                    next.Add(sourceId);
                }
            }

            frontier = next;
        }

        return results;
    }

    private static IEnumerable<GraphEdge> EnumerateBothDirections(InMemoryGraphCache cache, Guid nodeId) =>
        cache.GetOutgoing(nodeId).Concat(cache.GetIncoming(nodeId));
}
