using Context.Domain.Entities;

namespace Context.Memory.Abstractions;

/// <summary>
/// Produces a <see cref="SessionSummaryChunk"/> for a session by reading its recorded events
/// and (when a real LLM provider is configured) asking it to summarize them into the
/// Completed/Discovered/Decisions/Changed-files/Open-issues/Next-steps structure.
/// </summary>
public interface ISessionSummarizer
{
    /// <summary>
    /// Summarizes the events after <paramref name="since"/> (exclusive) through the most recent
    /// event into a single chunk. <paramref name="since"/> null means "summarize every event
    /// recorded for the session" (the whole-session behavior this method had before chunking).
    /// </summary>
    Task<SessionSummaryChunk> SummarizeAsync(Guid projectId, Guid sessionId, DateTimeOffset? since = null, CancellationToken ct = default);
}
