using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Graph.Abstractions;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// Self-healing sweep for <see cref="GraphNodeKind.UnresolvedExternal"/> placeholder nodes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists:</b> <see cref="UpdateGraphJobHandler.ResolveTargetAsync"/> resolves a
/// cross-file relation's target against whatever <see cref="Symbol"/> rows exist in the DB at the
/// instant that file's <c>UpdateGraph</c> job happens to run. On a fresh/full index,
/// <c>FileSystemScanner</c> enumerates files alphabetically and <c>JobDispatcher</c> runs several
/// <c>UpdateGraph</c> jobs concurrently (both deliberately left as-is — see the task's own scope
/// note), so a caller file's job frequently executes before its callee's file has been indexed at
/// all. That relation then falls through to a synthetic <see cref="GraphNodeKind.UnresolvedExternal"/>
/// node instead of the real target, and nothing previously ever retried it: a later reindex of the
/// caller's file only re-enqueues <c>UpdateGraph</c> when THAT file's own symbols changed, which a
/// stale relation elsewhere never does. This handler is the retry: it re-attempts every
/// UnresolvedExternal placeholder in a project against the (by now hopefully more complete) Symbol
/// table, and repoints any GraphEdge that was settling for the placeholder onto the real node.
/// </para>
/// <para>
/// <b>Trigger:</b> <see cref="Indexing.IndexingPipeline.ReindexProjectAsync"/> enqueues one
/// <see cref="JobType.ResolveRelations"/> job per project right after its per-file loop finishes
/// (i.e. once every <c>UpdateGraph</c>/<c>EmbedChunk</c> job for that run has been enqueued) — see
/// its own remarks for why that point, rather than a true queue-drained signal, is what's
/// available and sufficient here.
/// </para>
/// </remarks>
public sealed class ResolveRelationsJobHandler(
    IGraphRepository graphs,
    ISymbolRepository symbols,
    IGraphStore graphStore,
    ILogger<ResolveRelationsJobHandler>? logger = null) : IJobHandler
{
    public JobType Type => JobType.ResolveRelations;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ResolveRelationsPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(ResolveRelationsPayload)}.");

        var placeholders = await graphs.GetNodesByKindAsync(payload.ProjectId, GraphNodeKind.UnresolvedExternal, ct)
            .ConfigureAwait(false);

        foreach (var placeholder in placeholders)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await TryResolveAsync(payload.ProjectId, placeholder, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One bad placeholder must not abort the sweep for every other one in the project;
                // it just stays UnresolvedExternal and gets another attempt on the next sweep.
                logger?.LogWarning(ex,
                    "Failed to resolve placeholder node {NodeId} ('{Label}') in project {ProjectId}; left unresolved.",
                    placeholder.Id, placeholder.Label, payload.ProjectId);
            }
        }
    }

    private async Task TryResolveAsync(Guid projectId, GraphNode placeholder, CancellationToken ct)
    {
        // placeholder.Label carries the original unresolved target name -- it's what
        // UpdateGraphJobHandler.ResolveTargetAsync synthesized the placeholder from.
        var resolved = await GraphRelationResolution.ResolveByNameAsync(symbols, projectId, placeholder.Label, limit: 5, ct)
            .ConfigureAwait(false);
        if (resolved is null)
        {
            return; // still no matching Symbol -- may genuinely be external/third-party; retried on the next sweep
        }

        var resolvedNode = GraphRelationResolution.ToGraphNode(resolved, projectId);

        var referencingEdges = await graphs.GetEdgesByNodeAsync(placeholder.Id, kinds: null, ct).ConfigureAwait(false);
        var retargeted = referencingEdges
            .Select(e => Retarget(e, placeholder.Id, resolvedNode.Id))
            .ToList();

        // Through IGraphStore (not IGraphRepository) so the already-warm per-project
        // InMemoryGraphCache -- what IGraphTraversal.BlastRadiusAsync actually reads -- is kept in
        // sync immediately, exactly as UpdateGraphJobHandler's own writes are. UpsertAsync also
        // finds-or-creates resolvedNode: it may not exist yet if this sweep is racing the resolved
        // symbol's own file's UpdateGraph job (harmless -- that job's own later upsert of the same
        // Id is idempotent).
        await graphStore.UpsertAsync([resolvedNode], retargeted, ct).ConfigureAwait(false);

        // Re-check right before deleting: another file's still-unresolved relation to the same
        // name shares this exact placeholder id (ResolveTargetAsync's synthetic id is deterministic
        // per project+name, not per relation), and a concurrent UpdateGraph job could have added a
        // fresh edge to it between the read above and this write.
        var stillReferenced = await graphs.GetEdgesByNodeAsync(placeholder.Id, kinds: null, ct).ConfigureAwait(false);
        if (stillReferenced.Count > 0)
        {
            return;
        }

        // Deleted via IGraphRepository directly, not IGraphStore: this node has zero edges left
        // (just confirmed above), so leaving its now-inert entry in the singleton
        // InMemoryGraphCache until that project's cache is next rebuilt is harmless -- every
        // traversal the cache serves is keyed off edges, and this node no longer has any.
        await graphs.DeleteNodeAsync(placeholder.Id, ct).ConfigureAwait(false);
    }

    private static GraphEdge Retarget(GraphEdge edge, Guid fromId, Guid toId) => new()
    {
        Id = edge.Id,
        ProjectId = edge.ProjectId,
        SourceNodeId = edge.SourceNodeId == fromId ? toId : edge.SourceNodeId,
        TargetNodeId = edge.TargetNodeId == fromId ? toId : edge.TargetNodeId,
        Kind = edge.Kind,
        OwningFileId = edge.OwningFileId,
        Weight = edge.Weight
    };
}
