namespace Context.Application.Notifications;

/// <summary>DI default for <see cref="IProjectActivityNotifier"/> - mirrors NullLogger's private-ctor/static-Instance shape.</summary>
public sealed class NullProjectActivityNotifier : IProjectActivityNotifier
{
    public static readonly NullProjectActivityNotifier Instance = new();

    private NullProjectActivityNotifier() { }

    public Task NotifyAsync(Guid projectId, ProjectActivityKind kind, CancellationToken ct) => Task.CompletedTask;
}
