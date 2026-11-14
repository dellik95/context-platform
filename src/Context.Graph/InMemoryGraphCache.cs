using Context.Domain.Entities;
using Context.Graph.Abstractions;

namespace Context.Graph;

/// <summary>
/// Warm, per-project in-memory view of the code graph that <see cref="GraphTraversal"/> reads
/// from. Holds adjacency in both directions so callers/callees, importers/imports, etc. can be
/// walked without scanning every edge. Built lazily (<see cref="CreateAsync"/>) and then updated
/// incrementally via <see cref="InvalidateOwningFile"/> + <see cref="Hydrate"/> as files are
/// re-indexed — never a full rebuild (spec §9.4: "a modified file must not require rebuilding
/// the entire graph").
/// </summary>
public sealed class InMemoryGraphCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, GraphNode> _nodesById = new();
    private readonly Dictionary<Guid, GraphEdge> _edgesById = new();
    private readonly Dictionary<Guid, List<GraphEdge>> _outgoing = new(); // SourceNodeId -> edges
    private readonly Dictionary<Guid, List<GraphEdge>> _incoming = new(); // TargetNodeId -> edges

    public InMemoryGraphCache(Guid projectId)
    {
        ProjectId = projectId;
    }

    public Guid ProjectId { get; }

    /// <summary>Builds a fresh cache for <paramref name="projectId"/> from persistence.</summary>
    public static async Task<InMemoryGraphCache> CreateAsync(
        Guid projectId, IGraphPersistence persistence, CancellationToken ct)
    {
        var cache = new InMemoryGraphCache(projectId);
        var snapshot = await persistence.GetAllAsync(projectId, ct).ConfigureAwait(false);
        cache.Hydrate(snapshot.Nodes, snapshot.Edges);
        return cache;
    }

    /// <summary>Adds or overwrites the given nodes/edges (idempotent by Id). Safe to call repeatedly.</summary>
    public void Hydrate(IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges)
    {
        lock (_gate)
        {
            foreach (var node in nodes)
                _nodesById[node.Id] = node;

            foreach (var edge in edges)
                AddEdgeInternal(edge);
        }
    }

    /// <summary>
    /// Removes every node owned by <paramref name="fileId"/>, plus any edge owned by it or
    /// left dangling once its endpoints are gone. Touches only that file's data.
    /// </summary>
    public void InvalidateOwningFile(Guid projectId, Guid fileId)
    {
        if (projectId != ProjectId)
            throw new ArgumentException(
                $"This cache holds project {ProjectId}; cannot invalidate project {projectId}.", nameof(projectId));

        lock (_gate)
        {
            var staleNodeIds = new HashSet<Guid>();
            foreach (var node in _nodesById.Values)
                if (node.OwningFileId == fileId)
                    staleNodeIds.Add(node.Id);

            var staleEdgeIds = new List<Guid>();
            foreach (var edge in _edgesById.Values)
                if (edge.OwningFileId == fileId
                    || staleNodeIds.Contains(edge.SourceNodeId)
                    || staleNodeIds.Contains(edge.TargetNodeId))
                    staleEdgeIds.Add(edge.Id);

            foreach (var edgeId in staleEdgeIds)
                RemoveEdgeInternal(edgeId);

            foreach (var nodeId in staleNodeIds)
                _nodesById.Remove(nodeId);
        }
    }

    /// <summary>
    /// Atomically replaces all nodes and edges owned by <paramref name="fileId"/> while preserving
    /// incoming cross-file edges owned by OTHER files that target surviving nodes owned by
    /// <paramref name="fileId"/>.
    /// </summary>
    public void ReplaceOwningFile(
        Guid projectId, Guid fileId, IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges)
    {
        if (projectId != ProjectId)
            throw new ArgumentException(
                $"This cache holds project {ProjectId}; cannot update project {projectId}.", nameof(projectId));

        var nodeList = nodes as IReadOnlyCollection<GraphNode> ?? nodes.ToList();
        var edgeList = edges as IReadOnlyCollection<GraphEdge> ?? edges.ToList();

        var incomingOwnedNodeIds = nodeList
            .Where(n => n.OwningFileId == fileId)
            .Select(n => n.Id)
            .ToHashSet();
        var incomingOwnedEdgeIds = edgeList
            .Where(e => e.OwningFileId == fileId)
            .Select(e => e.Id)
            .ToHashSet();

        lock (_gate)
        {
            var staleNodeIds = new HashSet<Guid>();
            foreach (var node in _nodesById.Values)
            {
                if (node.OwningFileId == fileId && !incomingOwnedNodeIds.Contains(node.Id))
                    staleNodeIds.Add(node.Id);
            }

            var staleEdgeIds = new List<Guid>();
            foreach (var edge in _edgesById.Values)
            {
                if ((edge.OwningFileId == fileId && !incomingOwnedEdgeIds.Contains(edge.Id))
                    || staleNodeIds.Contains(edge.SourceNodeId)
                    || staleNodeIds.Contains(edge.TargetNodeId))
                {
                    staleEdgeIds.Add(edge.Id);
                }
            }

            foreach (var edgeId in staleEdgeIds)
                RemoveEdgeInternal(edgeId);

            foreach (var nodeId in staleNodeIds)
                _nodesById.Remove(nodeId);

            foreach (var node in nodeList)
                _nodesById[node.Id] = node;

            foreach (var edge in edgeList)
                AddEdgeInternal(edge);
        }
    }

    public GraphNode? GetNode(Guid nodeId)
    {
        lock (_gate)
            return _nodesById.TryGetValue(nodeId, out var node) ? node : null;
    }

    public bool ContainsNode(Guid nodeId)
    {
        lock (_gate)
            return _nodesById.ContainsKey(nodeId);
    }

    /// <summary>Edges where <paramref name="nodeId"/> is the source.</summary>
    public IReadOnlyList<GraphEdge> GetOutgoing(Guid nodeId)
    {
        lock (_gate)
            return _outgoing.TryGetValue(nodeId, out var list) ? list.ToArray() : Array.Empty<GraphEdge>();
    }

    /// <summary>Edges where <paramref name="nodeId"/> is the target.</summary>
    public IReadOnlyList<GraphEdge> GetIncoming(Guid nodeId)
    {
        lock (_gate)
            return _incoming.TryGetValue(nodeId, out var list) ? list.ToArray() : Array.Empty<GraphEdge>();
    }

    public IReadOnlyList<GraphNode> AllNodes()
    {
        lock (_gate)
            return _nodesById.Values.ToArray();
    }

    private void AddEdgeInternal(GraphEdge edge)
    {
        RemoveEdgeInternal(edge.Id); // guard against duplicate re-hydration of the same edge id
        _edgesById[edge.Id] = edge;
        GetOrCreateList(_outgoing, edge.SourceNodeId).Add(edge);
        GetOrCreateList(_incoming, edge.TargetNodeId).Add(edge);
    }

    private void RemoveEdgeInternal(Guid edgeId)
    {
        if (!_edgesById.Remove(edgeId, out var existing))
            return;

        if (_outgoing.TryGetValue(existing.SourceNodeId, out var outList))
            outList.RemoveAll(e => e.Id == edgeId);

        if (_incoming.TryGetValue(existing.TargetNodeId, out var inList))
            inList.RemoveAll(e => e.Id == edgeId);
    }

    private static List<GraphEdge> GetOrCreateList(Dictionary<Guid, List<GraphEdge>> dict, Guid key)
    {
        if (!dict.TryGetValue(key, out var list))
        {
            list = new List<GraphEdge>();
            dict[key] = list;
        }
        return list;
    }
}
