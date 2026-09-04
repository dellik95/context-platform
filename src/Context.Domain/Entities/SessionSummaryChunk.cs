namespace Context.Domain.Entities;

/// <summary>
/// One append-only slice of a session's summary, covering a contiguous range of session
/// events (<see cref="FromEventAt"/>..<see cref="ToEventAt"/>). Chunks are never edited after
/// creation; <see cref="Summarization.SessionSummaryMerger"/> (Context.Memory) folds every
/// chunk for a session, oldest to newest, into the single current-picture row in
/// <see cref="SessionSummary"/>.
/// </summary>
public class SessionSummaryChunk
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public int SequenceNumber { get; set; }
    public DateTimeOffset FromEventAt { get; set; }
    public DateTimeOffset ToEventAt { get; set; }
    public int EventCount { get; set; }
    public string? Title { get; set; }
    public string? Completed { get; set; }
    public string? Discovered { get; set; }
    public string? Decisions { get; set; }
    public string? FilesReadJson { get; set; }
    public string? ChangedFilesJson { get; set; }
    public string? OpenIssues { get; set; }
    public string? NextSteps { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
}
