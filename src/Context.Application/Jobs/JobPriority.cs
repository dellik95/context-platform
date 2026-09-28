namespace Context.Application.Jobs;

public static class JobPriority
{
    public const int Background = 0;

    /// <summary>Memory extraction and session summaries. A project's lane is shared with indexing,
    /// whose EmbedChunk/UpdateGraph backlog can run to thousands of jobs, so session work queued at
    /// the same priority would wait behind all of it.</summary>
    public const int Session = 10;
}
