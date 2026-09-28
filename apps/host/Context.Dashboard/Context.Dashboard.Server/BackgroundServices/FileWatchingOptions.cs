namespace Context.Dashboard.Server.BackgroundServices;

public enum FileWatchMode
{
    /// <summary>Polling for roots on a network/VM file system (see <see cref="MountFileSystem"/>), events otherwise.</summary>
    Auto,
    Events,
    Polling,
}

/// <summary>Bound from <c>ContextPlatform:FileWatching</c>.</summary>
public sealed class FileWatchingOptions
{
    public FileWatchMode Mode { get; set; } = FileWatchMode.Auto;

    /// <summary>Pause between the end of one polling scan and the start of the next.</summary>
    public double PollingIntervalSeconds { get; set; } = 5;
}
