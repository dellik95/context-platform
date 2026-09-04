namespace Context.Application.Notifications;

/// <summary>Live snapshot of the job queue's status breakdown -- the same shape as
/// <c>SystemStatusDto</c>'s four jobsXxx fields (<c>Context.Host.Api.SystemEndpoints</c>), pushed
/// instead of polled.</summary>
public sealed record JobCounts(int Pending, int Running, int Failed, int Done);

/// <summary>
/// Fire-and-forget push of the job queue's aggregate status counts, so the Dashboard's System tab
/// can update its stat tiles live instead of polling <c>GET /api/system/status</c>. Host-wide, not
/// project-scoped -- the REST endpoint's job counts already aggregate across every project's jobs,
/// not just the one currently open, so this notifier reports the same host-wide figure. Mirrors
/// <see cref="IProjectActivityNotifier"/>/<c>Context.Application.Indexing.IIndexingProgressReporter</c>'s
/// seam: the default registration is a no-op, Context.Host overrides it with a SignalR-backed one.
/// </summary>
public interface IJobCountsNotifier
{
    Task NotifyAsync(JobCounts counts, CancellationToken ct);
}
