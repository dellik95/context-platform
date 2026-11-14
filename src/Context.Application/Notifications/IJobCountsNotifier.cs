namespace Context.Application.Notifications;

/// <summary>Live snapshot of the job queue's status breakdown -- the same shape as
/// <c>SystemStatusDto</c>'s four jobsXxx fields (<c>Context.Dashboard.Server.Api.SystemEndpoints</c>), pushed
/// instead of polled.</summary>
public sealed record JobCounts(int Pending, int Running, int Failed, int Done);

/// <summary>
/// Fire-and-forget push of one project's job status counts, so the Dashboard's System tab can
/// update its stat tiles live instead of polling <c>GET /api/system/status</c>. Project-scoped,
/// matching that REST endpoint's own (now project-scoped) counts -- broadcast to that project's
/// SignalR group only, same as <c>IndexingHubNotifier.ReindexProgressAsync</c>. A host-wide
/// maintenance job (no <c>Job.ProjectId</c>) has no project tile to update, so callers simply
/// don't notify for it. Mirrors
/// <see cref="IProjectActivityNotifier"/>/<c>Context.Application.Indexing.IIndexingProgressReporter</c>'s
/// seam: the default registration is a no-op, Context.Dashboard.Server overrides it with a SignalR-backed one.
/// </summary>
public interface IJobCountsNotifier
{
    Task NotifyAsync(Guid projectId, JobCounts counts, CancellationToken ct);
}
