using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Repositories;

public class GraphRepository(ContextDbContext db) : IGraphRepository
{
    public Task<GraphNode?> GetNodeByIdAsync(Guid id, CancellationToken ct) =>
        db.GraphNodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, ct);

    public async Task<IReadOnlyList<GraphNode>> GetNodesByOwningFileAsync(Guid fileId, CancellationToken ct) =>
        await db.GraphNodes.AsNoTracking().Where(n => n.OwningFileId == fileId).ToListAsync(ct);

    public async Task<IReadOnlyList<GraphEdge>> GetEdgesByNodeAsync(Guid nodeId, GraphEdgeKind[]? kinds, CancellationToken ct)
    {
        var query = db.GraphEdges.AsNoTracking()
            .Where(e => e.SourceNodeId == nodeId || e.TargetNodeId == nodeId);

        if (kinds is { Length: > 0 })
        {
            query = query.Where(e => kinds.Contains(e.Kind));
        }

        return await query.ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GraphNode>> GetNodesByKindAsync(Guid projectId, GraphNodeKind kind, CancellationToken ct) =>
        await db.GraphNodes.AsNoTracking().Where(n => n.ProjectId == projectId && n.Kind == kind).ToListAsync(ct);

    public async Task UpsertAsync(IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges, CancellationToken ct)
    {
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        foreach (var node in nodes)
        {
            var existing = await db.GraphNodes.FirstOrDefaultAsync(n => n.Id == node.Id, ct);
            if (existing is null)
            {
                if (node.Id == Guid.Empty)
                {
                    node.Id = Guid.NewGuid();
                }

                db.GraphNodes.Add(node);
            }
            else
            {
                existing.Kind = node.Kind;
                existing.RefId = node.RefId;
                existing.Label = node.Label;
                existing.OwningFileId = node.OwningFileId;
            }
        }

        await db.SaveChangesAsync(ct);

        foreach (var edge in edges)
        {
            var existing = await db.GraphEdges.FirstOrDefaultAsync(e => e.Id == edge.Id, ct);
            if (existing is null)
            {
                if (edge.Id == Guid.Empty)
                {
                    edge.Id = Guid.NewGuid();
                }

                db.GraphEdges.Add(edge);
            }
            else
            {
                existing.SourceNodeId = edge.SourceNodeId;
                existing.TargetNodeId = edge.TargetNodeId;
                existing.Kind = edge.Kind;
                existing.OwningFileId = edge.OwningFileId;
                existing.Weight = edge.Weight;
            }
        }

        await db.SaveChangesAsync(ct);
        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }
    }

    public async Task ReplaceForFileAsync(
        Guid projectId, Guid fileId, IEnumerable<GraphNode> nodes, IEnumerable<GraphEdge> edges, CancellationToken ct)
    {
        var nodeList = nodes as IReadOnlyCollection<GraphNode> ?? nodes.ToList();
        var edgeList = edges as IReadOnlyCollection<GraphEdge> ?? edges.ToList();

        var keptOwnedNodeIds = nodeList.Where(n => n.OwningFileId == fileId).Select(n => n.Id).ToHashSet();
        var keptOwnedEdgeIds = edgeList.Where(e => e.OwningFileId == fileId).Select(e => e.Id).ToHashSet();

        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        var existingOwnedNodes = await db.GraphNodes
            .Where(n => n.ProjectId == projectId && n.OwningFileId == fileId)
            .ToListAsync(ct);
        var removedNodes = existingOwnedNodes.Where(n => !keptOwnedNodeIds.Contains(n.Id)).ToList();
        var removedNodeIds = removedNodes.Select(n => n.Id).ToHashSet();

        var existingFileEdges = await db.GraphEdges
            .Where(e => e.ProjectId == projectId
                && (e.OwningFileId == fileId || removedNodeIds.Contains(e.SourceNodeId) || removedNodeIds.Contains(e.TargetNodeId)))
            .ToListAsync(ct);
        var staleEdges = existingFileEdges
            .Where(e => (e.OwningFileId == fileId && !keptOwnedEdgeIds.Contains(e.Id))
                || removedNodeIds.Contains(e.SourceNodeId)
                || removedNodeIds.Contains(e.TargetNodeId))
            .ToList();

        if (staleEdges.Count > 0)
        {
            db.GraphEdges.RemoveRange(staleEdges);
        }

        if (removedNodes.Count > 0)
        {
            db.GraphNodes.RemoveRange(removedNodes);
        }

        if (staleEdges.Count > 0 || removedNodes.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        await UpsertAsync(nodeList, edgeList, ct);

        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }
    }

    public async Task InvalidateByOwningFileAsync(Guid projectId, Guid fileId, CancellationToken ct)
    {
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        var nodes = await db.GraphNodes
            .Where(n => n.ProjectId == projectId && n.OwningFileId == fileId)
            .ToListAsync(ct);
        var nodeIds = nodes.Select(n => n.Id).ToHashSet();

        // Restrict FK on GraphEdge.Source/TargetNodeId means any edge still pointing at one of
        // these nodes blocks the node delete below — not just edges this file owns, but
        // cross-file reference edges owned by whichever file introduced the reference.
        var edges = await db.GraphEdges
            .Where(e => e.ProjectId == projectId
                && (e.OwningFileId == fileId || nodeIds.Contains(e.SourceNodeId) || nodeIds.Contains(e.TargetNodeId)))
            .ToListAsync(ct);
        db.GraphEdges.RemoveRange(edges);

        db.GraphNodes.RemoveRange(nodes);

        await db.SaveChangesAsync(ct);
        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }
    }

    public async Task DeleteNodeAsync(Guid id, CancellationToken ct)
    {
        var node = await db.GraphNodes.FirstOrDefaultAsync(n => n.Id == id, ct);
        if (node is null)
        {
            return;
        }

        db.GraphNodes.Remove(node);
        await db.SaveChangesAsync(ct);
    }
}
