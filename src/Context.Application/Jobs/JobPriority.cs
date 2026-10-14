namespace Context.Application.Jobs;

public static class JobPriority
{
    public const int Background = 0;

    /// <summary>Memory extraction, session summaries and reconsolidation (scheduled or dashboard). A
    /// project's lane is shared with indexing, whose EmbedChunk/UpdateGraph backlog can run to
    /// thousands of jobs, so work queued at the same priority would wait behind all of it.</summary>
    public const int Session = 10;

    /// <summary>Hook capture. Cheap, and it must not wait behind a slow LLM job already queued at <see cref="Session"/>.</summary>
    public const int Hook = 20;
}
