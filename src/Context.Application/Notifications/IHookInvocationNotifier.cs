using Context.Domain.Enums;

namespace Context.Application.Notifications;

/// <summary>One hook endpoint call, pushed live to the Dashboard so its "Hook invocations" panel
/// updates without polling <c>GET /api/hooks/status</c>. <c>TotalCount</c> is the project's running
/// total after this invocation, so the panel's count badge can be patched in place from the event
/// alone.</summary>
public sealed record HookInvocationEvent(HookType Type, string PayloadJson, DateTimeOffset ReceivedAt, int TotalCount);

/// <summary>
/// Fire-and-forget push of one hook invocation, project-scoped, mirroring
/// <see cref="IJobCountsNotifier"/>'s seam exactly: the default registration is a no-op,
/// Context.Dashboard.Server overrides it with a SignalR-backed one.
/// </summary>
public interface IHookInvocationNotifier
{
    Task NotifyAsync(Guid projectId, HookInvocationEvent evt, CancellationToken ct);
}
