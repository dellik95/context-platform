using Context.Domain.Entities;
using Context.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// Placeholder handler for <see cref="JobType.ResolveRelations"/>. Nothing in the pipeline
/// currently enqueues this job type (see <see cref="Indexing.IndexingPipeline"/> and
/// <see cref="UpdateGraphJobHandler"/>, which resolve what they can inline at graph-build time),
/// but it is registered so <see cref="JobDispatcher"/> never hits an "unhandled JobType" for a
/// value CONTRACTS.md's <c>JobType</c> enum (§1) declares.
/// </summary>
/// <remarks>
/// TODO: a real implementation needs to re-attempt resolving existing
/// <see cref="Context.Domain.Enums.GraphNodeKind.UnresolvedExternal"/> nodes against symbols
/// discovered by later indexing passes, which in turn needs an edge-repoint capability
/// (rewriting an edge's <c>TargetNodeId</c> once the real target is known) that
/// <c>IGraphRepository</c> does not currently expose — out of scope for this pass since it would
/// mean widening a Context.Infrastructure interface, a different project.
/// </remarks>
public sealed class ResolveRelationsJobHandler(ILogger<ResolveRelationsJobHandler>? logger = null) : IJobHandler
{
    public JobType Type => JobType.ResolveRelations;

    public Task HandleAsync(Job job, CancellationToken ct)
    {
        logger?.LogDebug("ResolveRelations job {JobId} received but is a no-op in this build (see class remarks).", job.Id);
        return Task.CompletedTask;
    }
}
