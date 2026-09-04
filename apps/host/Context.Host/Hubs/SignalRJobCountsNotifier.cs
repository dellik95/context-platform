using Context.Application.Notifications;

namespace Context.Host.Hubs;

/// <summary>Overrides Context.Application's <see cref="NullJobCountsNotifier"/> default (see Program.cs registration order).</summary>
public sealed class SignalRJobCountsNotifier(IndexingHubNotifier hubNotifier) : IJobCountsNotifier
{
    public Task NotifyAsync(JobCounts counts, CancellationToken ct) =>
        hubNotifier.JobCountsChangedAsync(counts, ct);
}
