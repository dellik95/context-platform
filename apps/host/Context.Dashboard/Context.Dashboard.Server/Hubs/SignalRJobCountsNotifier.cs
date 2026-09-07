using Context.Application.Notifications;

namespace Context.Dashboard.Server.Hubs;

/// <summary>Overrides Context.Application's <see cref="NullJobCountsNotifier"/> default (see Program.cs registration order).</summary>
public sealed class SignalRJobCountsNotifier(IndexingHubNotifier hubNotifier) : IJobCountsNotifier
{
    public Task NotifyAsync(Guid projectId, JobCounts counts, CancellationToken ct) =>
        hubNotifier.JobCountsChangedAsync(projectId, counts, ct);
}
