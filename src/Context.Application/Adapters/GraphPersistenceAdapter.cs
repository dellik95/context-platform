using Context.Domain.Entities;
using Context.Graph.Abstractions;
using Context.Infrastructure;
using Context.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Context.Application.Adapters;

/// <summary>
/// Adapts Context.Graph's persistence-agnostic <see cref="IGraphPersistence"/> seam onto
/// Context.Infrastructure. Most operations delegate to a freshly-constructed
/// <see cref="GraphRepository"/>; but that repository's own interface,
/// <c>IGraphRepository</c>, has no "every node/edge for a project" query (it only supports point
/// lookups and per-owning-file scans — see its own remarks), which
/// <see cref="IGraphPersistence.GetAllAsync"/> needs to cold-hydrate <c>InMemoryGraphCache</c>.
/// Rather than widen <c>IGraphRepository</c> (out of scope: that interface belongs to
/// Context.Infrastructure), this adapter runs that one extra query directly against the
/// <see cref="ContextDbContext"/> it already has open.
/// </summary>
/// <remarks>
/// <b>Lifetime:</b> this adapter is registered as a <b>singleton</b> because Context.Graph's
/// <c>GraphCacheProvider</c>/<c>GraphStore</c> (its consumers, via <c>AddContextGraph</c>) are
/// themselves singletons (the warm per-project graph cache must outlive any one request). It
/// cannot simply constructor-inject the pooled, Scoped <see cref="ContextDbContext"/> the way the
/// other Context.Application adapters do — DI would reject a Scoped dependency captured by a
/// singleton. Instead it takes an <see cref="IDbContextFactory{TContext}"/> (registered
/// alongside the pool by <c>AddContextApplication</c>, targeting the same database file) and opens
/// a short-lived context per call, which is the standard EF Core pattern for exactly this
/// singleton-needs-a-DbContext situation.
/// </remarks>
public sealed class GraphPersistenceAdapter(IDbContextFactory<ContextDbContext> dbContextFactory) : IGraphPersistence
{
    public async Task AddNodesAsync(IEnumerable<GraphNode> nodes, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await new GraphRepository(db).UpsertAsync(nodes, [], ct).ConfigureAwait(false);
    }

    public async Task AddEdgesAsync(IEnumerable<GraphEdge> edges, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await new GraphRepository(db).UpsertAsync([], edges, ct).ConfigureAwait(false);
    }

    public async Task RemoveByOwningFileAsync(Guid projectId, Guid fileId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await new GraphRepository(db).InvalidateByOwningFileAsync(projectId, fileId, ct).ConfigureAwait(false);
    }

    public async Task<GraphSnapshot> GetAllAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var nodes = await db.GraphNodes.AsNoTracking()
            .Where(n => n.ProjectId == projectId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var edges = await db.GraphEdges.AsNoTracking()
            .Where(e => e.ProjectId == projectId)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new GraphSnapshot(nodes, edges);
    }
}
