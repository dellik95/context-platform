namespace Context.Application.Notifications;

/// <summary>Kind of project-scoped activity a Dashboard panel might want to react to live.</summary>
public enum ProjectActivityKind
{
    SessionSummaryUpdated,
    MemoryChanged,
}

/// <summary>
/// Fire-and-forget notification for "something changed for this project, a live view might want
/// to refresh" - deliberately coarse (no entity id) so Context.Application never has to know
/// whether anything is even listening. Mirrors <c>Context.Application.Indexing.IIndexingProgressReporter</c>'s
/// seam: the default registration is a no-op, Context.Dashboard.Server overrides it with a SignalR-backed one.
/// </summary>
public interface IProjectActivityNotifier
{
    Task NotifyAsync(Guid projectId, ProjectActivityKind kind, CancellationToken ct);
}
