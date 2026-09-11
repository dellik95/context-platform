namespace Context.Application.Notifications;

/// <summary>DI default for <see cref="IHookInvocationNotifier"/> - mirrors NullLogger's private-ctor/static-Instance shape.</summary>
public sealed class NullHookInvocationNotifier : IHookInvocationNotifier
{
    public static readonly NullHookInvocationNotifier Instance = new();

    private NullHookInvocationNotifier() { }

    public Task NotifyAsync(Guid projectId, HookInvocationEvent evt, CancellationToken ct) => Task.CompletedTask;
}
