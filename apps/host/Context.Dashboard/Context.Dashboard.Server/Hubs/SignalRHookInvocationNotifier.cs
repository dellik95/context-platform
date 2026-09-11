using Context.Application.Notifications;

namespace Context.Dashboard.Server.Hubs;

/// <summary>Overrides Context.Application's <see cref="NullHookInvocationNotifier"/> default (see Program.cs registration order).</summary>
public sealed class SignalRHookInvocationNotifier(IndexingHubNotifier hubNotifier) : IHookInvocationNotifier
{
    public Task NotifyAsync(Guid projectId, HookInvocationEvent evt, CancellationToken ct) =>
        hubNotifier.HookInvokedAsync(projectId, evt, ct);
}
