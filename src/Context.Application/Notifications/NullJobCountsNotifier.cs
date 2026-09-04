namespace Context.Application.Notifications;

/// <summary>DI default for <see cref="IJobCountsNotifier"/> - mirrors NullLogger's private-ctor/static-Instance shape.</summary>
public sealed class NullJobCountsNotifier : IJobCountsNotifier
{
    public static readonly NullJobCountsNotifier Instance = new();

    private NullJobCountsNotifier() { }

    public Task NotifyAsync(JobCounts counts, CancellationToken ct) => Task.CompletedTask;
}
