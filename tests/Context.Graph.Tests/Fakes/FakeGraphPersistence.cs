using Context.Domain.Entities;
using Context.Graph.Abstractions;

namespace Context.Graph.Tests.Fakes;

/// <summary>
/// Minimal in-memory <see cref="IGraphPersistence"/> stand-in for tests that only need
/// <see cref="InMemoryGraphCache.CreateAsync"/> to hydrate from "persisted" data — no EF Core,
/// no Context.Infrastructure dependency (Context.Graph must not reference either).
/// </summary>
public sealed class FakeGraphPersistence : IGraphPersistence
{
    private readonly List<GraphNode> _nodes = new();
    private readonly List<GraphEdge> _edges = new();

    public IReadOnlyList<GraphNode> Nodes => _nodes;
    public IReadOnlyList<GraphEdge> Edges => _edges;

    public FakeGraphPersistence Seed(IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges)
    {
        _nodes.AddRange(nodes);
        _edges.AddRange(edges);
        return this;
    }

    public Task AddNodesAsync(IEnumerable<GraphNode> nodes, CancellationToken ct)
    {
        _nodes.AddRange(nodes);
        return Task.CompletedTask;
    }

    public Task AddEdgesAsync(IEnumerable<GraphEdge> edges, CancellationToken ct)
    {
        _edges.AddRange(edges);
        return Task.CompletedTask;
    }

    public Task RemoveByOwningFileAsync(Guid projectId, Guid fileId, CancellationToken ct)
    {
        _nodes.RemoveAll(n => n.ProjectId == projectId && n.OwningFileId == fileId);
        _edges.RemoveAll(e => e.ProjectId == projectId && e.OwningFileId == fileId);
        return Task.CompletedTask;
    }

    public Task ReplaceForFileAsync(
        Guid projectId, Guid fileId, IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges, CancellationToken ct)
    {
        var nodeList = nodes.ToList();
        var edgeList = edges.ToList();
        var keptOwnedNodeIds = nodeList.Where(n => n.OwningFileId == fileId).Select(n => n.Id).ToHashSet();
        var keptOwnedEdgeIds = edgeList.Where(e => e.OwningFileId == fileId).Select(e => e.Id).ToHashSet();

        var removedNodeIds = _nodes
            .Where(n => n.ProjectId == projectId && n.OwningFileId == fileId && !keptOwnedNodeIds.Contains(n.Id))
            .Select(n => n.Id)
            .ToHashSet();

        _edges.RemoveAll(e => e.ProjectId == projectId && (
            (e.OwningFileId == fileId && !keptOwnedEdgeIds.Contains(e.Id))
            || removedNodeIds.Contains(e.SourceNodeId)
            || removedNodeIds.Contains(e.TargetNodeId)));
        _nodes.RemoveAll(n => removedNodeIds.Contains(n.Id));

        foreach (var node in nodeList)
        {
            _nodes.RemoveAll(n => n.Id == node.Id);
            _nodes.Add(node);
        }

        foreach (var edge in edgeList)
        {
            _edges.RemoveAll(e => e.Id == edge.Id);
            _edges.Add(edge);
        }

        return Task.CompletedTask;
    }

    public Task<GraphSnapshot> GetAllAsync(Guid projectId, CancellationToken ct)
    {
        var snapshot = new GraphSnapshot(
            _nodes.Where(n => n.ProjectId == projectId).ToArray(),
            _edges.Where(e => e.ProjectId == projectId).ToArray());
        return Task.FromResult(snapshot);
    }
}
