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

        var (neighbors, _) = ExpandNeighborhood(cache, nodeId, kinds, hops, ct);
        return Task.FromResult<IReadOnlyList<GraphNode>>(neighbors);
    }

    public Task<GraphNeighborhood> NeighborhoodAsync(
        Guid nodeId, GraphEdgeKind[]? kinds, int hops, CancellationToken ct)
    {
        var cache = _cacheProvider.FindCacheContaining(nodeId);
        var originNode = cache?.GetNode(nodeId);
        if (cache is null || originNode is null || hops <= 0)
            return Task.FromResult(new GraphNeighborhood(Array.Empty<GraphNode>(), Array.Empty<GraphEdgeDto>()));

        var (neighbors, visitedIds) = ExpandNeighborhood(cache, nodeId, kinds, hops, ct);

        // The origin node itself has to be in Nodes too, or its edges to hop-1 neighbors would
        // fail the "both endpoints in the set" test below and the graph would render disconnected.
        var nodes = new List<GraphNode>(neighbors.Count + 1) { originNode };
        nodes.AddRange(neighbors);

        var kindFilter = kinds is { Length: > 0 } ? new HashSet<GraphEdgeKind>(kinds) : null;
        var edges = CollectEdgesWithinSet(cache, visitedIds, kindFilter);

        return Task.FromResult(new GraphNeighborhood(nodes, edges));
    }

    public async Task<GraphNeighborhood> FullGraphAsync(Guid projectId, GraphEdgeKind[]? kinds, CancellationToken ct)
    {
        var cache = await _cacheProvider.GetOrCreateAsync(projectId, ct).ConfigureAwait(false);
        var nodes = cache.AllNodes();
        var nodeIds = new HashSet<Guid>(nodes.Select(n => n.Id));

        var kindFilter = kinds is { Length: > 0 } ? new HashSet<GraphEdgeKind>(kinds) : null;
        var edges = CollectEdgesWithinSet(cache, nodeIds, kindFilter);

        return new GraphNeighborhood(nodes, edges);
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

        // For container nodes (File, Class, Interface, Struct, Enum), expand outgoing Contains edges
        // into the seed set so querying a type rolls up callers and tests targeting any of its
        // contained methods, properties, or fields.
        var seedIds = ExpandContainerSeeds(cache, symbolNodeId);

        // Who depends on this symbol (or its contained members): walk Calls/References/Implements/Extends backwards.
        var callers = ReverseWalk(cache, seedIds, CallerEdgeKinds, maxHops, ct);

        // Subtypes/implementations specifically: same walk restricted to Implements/Extends.
        var implementers = ReverseWalk(cache, seedIds, ImplementerEdgeKinds, maxHops, ct);

        // Tests that cover the symbol, its contained members, or any of its (transitive) callers.
        var coverageTargets = new HashSet<Guid>(seedIds);
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

    private static List<Guid> ExpandContainerSeeds(InMemoryGraphCache cache, Guid rootId)
    {
        var seeds = new List<Guid> { rootId };
        var rootNode = cache.GetNode(rootId);
        if (rootNode?.Kind is not (GraphNodeKind.File or GraphNodeKind.Module or GraphNodeKind.Class or GraphNodeKind.Interface or GraphNodeKind.Type))
        {
            return seeds;
        }

        var visited = new HashSet<Guid> { rootId };
        var queue = new Queue<Guid>();
        queue.Enqueue(rootId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            foreach (var edge in cache.GetOutgoing(currentId))
            {
                if (edge.Kind != GraphEdgeKind.Contains)
                    continue;

                if (!visited.Add(edge.TargetNodeId))
                    continue;

                if (cache.GetNode(edge.TargetNodeId) is not null)
                {
                    seeds.Add(edge.TargetNodeId);
                    queue.Enqueue(edge.TargetNodeId);
                }
            }
        }

        return seeds;
    }

    /// <summary>Walks incoming edges of the given kinds backwards (target -> source), BFS up to maxHops.</summary>
    private static IReadOnlyList<ImpactNode> ReverseWalk(
        InMemoryGraphCache cache, IReadOnlyList<Guid> seedIds, IReadOnlyCollection<GraphEdgeKind> kinds, int maxHops, CancellationToken ct)
    {
        var results = new List<ImpactNode>();
        var visited = new HashSet<Guid>(seedIds);
        var pathById = new Dictionary<Guid, string>(seedIds.Count);
        var frontier = new List<Guid>(seedIds);

        foreach (var seedId in seedIds)
            pathById[seedId] = cache.GetNode(seedId)?.Label ?? seedId.ToString();

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

    /// <summary>
    /// Shared BFS hop-expansion behind both <see cref="NeighborsAsync"/> and
    /// <see cref="NeighborhoodAsync"/>: walks both edge directions from <paramref name="nodeId"/>
    /// up to <paramref name="hops"/> hops, honoring an optional edge-kind filter. Returns the
    /// discovered neighbor nodes (excluding the origin) plus the full visited-id set (including
    /// the origin) that <see cref="NeighborhoodAsync"/> needs to collect edges within.
    /// </summary>
    private static (List<GraphNode> Neighbors, HashSet<Guid> VisitedIds) ExpandNeighborhood(
        InMemoryGraphCache cache, Guid nodeId, GraphEdgeKind[]? kinds, int hops, CancellationToken ct)
    {
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

        return (result, visited);
    }

    /// <summary>
    /// Every edge (as a <see cref="GraphEdgeDto"/> projection) whose source AND target are both in
    /// <paramref name="nodeIds"/>. Enumerating only outgoing edges per node visits each edge
    /// exactly once (an edge's source owns it in <see cref="InMemoryGraphCache"/>'s adjacency).
    /// </summary>
    private static List<GraphEdgeDto> CollectEdgesWithinSet(
        InMemoryGraphCache cache, HashSet<Guid> nodeIds, HashSet<GraphEdgeKind>? kindFilter)
    {
        var edges = new List<GraphEdgeDto>();

        foreach (var nodeId in nodeIds)
        {
            foreach (var edge in cache.GetOutgoing(nodeId))
            {
                if (kindFilter is not null && !kindFilter.Contains(edge.Kind))
                    continue;

                if (!nodeIds.Contains(edge.TargetNodeId))
                    continue;

                edges.Add(new GraphEdgeDto(edge.SourceNodeId, edge.TargetNodeId, edge.Kind));
            }
        }

        return edges;
    }
}
