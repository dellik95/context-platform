namespace Context.Application.Indexing;

/// <summary>Tells live dashboard views that a project's index figures (files, symbols, memories) changed.
/// The default registration does nothing; Context.Dashboard.Server pushes the status over SignalR.</summary>
public interface IProjectStatusPublisher
{
    Task PublishAsync(Guid projectId, CancellationToken ct);
}

public sealed class NullProjectStatusPublisher : IProjectStatusPublisher
{
    public static readonly NullProjectStatusPublisher Instance = new();

    private NullProjectStatusPublisher()
    {
    }

    public Task PublishAsync(Guid projectId, CancellationToken ct) => Task.CompletedTask;
}
