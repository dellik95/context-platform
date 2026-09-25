namespace Context.Application.Jobs;

public sealed class JobDispatcherOptions
{
    /// <summary>
    /// Jobs one project's lane runs at once (host-wide maintenance jobs get a lane of the same
    /// size). There is deliberately no host-wide cap on top: one would let a project whose handlers
    /// are all slow or stuck hold every slot and starve the rest. Low by default because handlers
    /// block on SQLite/file I/O (see JobDispatcher's remarks) and lanes multiply with projects.
    /// </summary>
    public int WorkersPerProject { get; set; } = Math.Clamp(Environment.ProcessorCount / 4, 2, 4);
}
