namespace Context.Application.Jobs;

/// <summary>Sizes the dispatcher's <see cref="JobWorkerPool"/>. Both stay modest because handlers
/// block on SQLite/file I/O (see JobDispatcher's remarks).</summary>
public sealed class JobDispatcherOptions
{
    /// <summary>Slots each project's lane (and the host-wide maintenance lane) always has to itself,
    /// however busy the others are, so a project whose handlers are all slow or stuck can't starve the rest.</summary>
    public int WorkersPerProject { get; set; } = 2;

    /// <summary>Host-wide slots on top, borrowed by lanes with a backlog and split evenly between them:
    /// a lone busy project gets them all while the others are idle.</summary>
    public int SharedWorkers { get; set; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
}
