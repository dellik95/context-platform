using Context.Domain.Entities;

namespace Context.Memory.Abstractions;

/// <summary>
/// Produces a <see cref="SessionSummary"/> for a session by reading its recorded events
/// and (when a real LLM provider is configured) asking it to summarize them into the
/// Completed/Discovered/Decisions/Changed-files/Open-issues/Next-steps structure.
/// </summary>
public interface ISessionSummarizer
{
    Task<SessionSummary> SummarizeAsync(Guid sessionId, CancellationToken ct);
}
