using Context.Application.Notifications;

namespace Context.Dashboard.Server.Hubs;

/// <summary>Overrides Context.Application's <see cref="NullProjectActivityNotifier"/> default (see Program.cs registration order).</summary>
public sealed class SignalRProjectActivityNotifier(IndexingHubNotifier hubNotifier) : IProjectActivityNotifier
{
    public Task NotifyAsync(Guid projectId, ProjectActivityKind kind, CancellationToken ct) =>
        hubNotifier.ProjectActivityAsync(projectId, kind, ct);
}
