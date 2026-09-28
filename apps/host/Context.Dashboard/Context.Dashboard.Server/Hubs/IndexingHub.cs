using Context.Dashboard.Server.Security;
using Context.Domain.Entities;
using Microsoft.AspNetCore.SignalR;

namespace Context.Dashboard.Server.Hubs;

/// <summary>
/// Live indexing/project-status updates for the Dashboard (spec §13: "UI must receive indexing
/// updates through SignalR rather than aggressive polling"). Clients join a per-project group;
/// <see cref="IndexingHubNotifier"/> is the only thing that ever broadcasts to it.
/// </summary>
public sealed class IndexingHub : Hub
{
    public Task JoinProject(Guid projectId)
    {
        // HubBearerTokenAuthMiddleware stashed the project this connection's token resolved to.
        // SignalR keeps that HttpContext's Items for the connection's lifetime on every transport
        // (long polling copies them into its per-connection clone).
        if (Context.GetHttpContext()?.Items[CurrentProjectAccessor.HttpContextItemKey] is not Project project
            || project.Id != projectId)
        {
            throw new HubException("This connection's bearer token does not authorize access to the requested project.");
        }

        return Groups.AddToGroupAsync(Context.ConnectionId, GroupName(projectId));
    }

    public Task LeaveProject(Guid projectId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(projectId));

    public static string GroupName(Guid projectId) => $"project:{projectId}";
}

/// <summary>Thin, typed wrapper so callers (e.g. <c>ProjectEndpoints</c>) don't hand-roll group/method names.</summary>
public sealed class IndexingHubNotifier(IHubContext<IndexingHub> hub)
{
    public Task ProjectStatusChangedAsync(Guid projectId, object statusDto, CancellationToken ct) =>
        hub.Clients.Group(IndexingHub.GroupName(projectId)).SendAsync("ProjectStatusChanged", statusDto, ct);

    public Task ReindexProgressAsync(Guid projectId, Context.Application.Indexing.ReindexProgressDto progress, CancellationToken ct) =>
        hub.Clients.Group(IndexingHub.GroupName(projectId)).SendAsync("ReindexProgress", progress, ct);

    public Task ProjectActivityAsync(Guid projectId, Context.Application.Notifications.ProjectActivityKind kind, CancellationToken ct) =>
        hub.Clients.Group(IndexingHub.GroupName(projectId)).SendAsync("ProjectActivity", new { projectId, kind = kind.ToString() }, ct);

    /// <summary>Per-project push of the job queue's status breakdown — see
    /// <see cref="Context.Application.Notifications.IJobCountsNotifier"/>. Broadcasts to just that
    /// project's group, matching <c>GET /api/system/status</c>'s own (now project-scoped) job
    /// counts.</summary>
    public Task JobCountsChangedAsync(Guid projectId, Context.Application.Notifications.JobCounts counts, CancellationToken ct) =>
        hub.Clients.Group(IndexingHub.GroupName(projectId)).SendAsync("JobCountsChanged", counts, ct);

    /// <summary>Per-project push of one hook endpoint call — see
    /// <see cref="Context.Application.Notifications.IHookInvocationNotifier"/>.</summary>
    public Task HookInvokedAsync(Guid projectId, Context.Application.Notifications.HookInvocationEvent evt, CancellationToken ct) =>
        hub.Clients.Group(IndexingHub.GroupName(projectId)).SendAsync("HookInvoked", evt, ct);
}
