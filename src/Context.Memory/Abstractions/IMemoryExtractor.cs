using Context.Domain.Entities;

namespace Context.Memory.Abstractions;

/// <summary>
/// Given some session material (a batch of raw events, a compaction summary, or a summary chunk),
/// asks an LLM (or falls back to a heuristic) to propose candidate durable memories, embeds and
/// deduplicates each candidate against the project's existing memories, and writes the result (new
/// memories, reinforcements, and supersessions) via <see cref="IMemoryStore"/>. Every method
/// returns the memories newly inserted or reinforced during the call.
/// </summary>
public interface IMemoryExtractor
{
    /// <summary>
    /// Extracts and persists candidate memories from <paramref name="chunk"/> — one append-only
    /// slice of a session's summary, not its full merged history.
    /// Returns the memories that were newly inserted or reinforced during this call
    /// (superseded predecessors are not included, but remain persisted with
    /// <see cref="Memory.SupersededById"/> set — they are not deleted).
    /// </summary>
    Task<IReadOnlyList<Context.Domain.Entities.Memory>> ExtractAsync(Guid projectId, SessionSummaryChunk chunk, CancellationToken ct);

    /// <summary>Extracts from one batch of raw session events, oldest first.</summary>
    Task<IReadOnlyList<Context.Domain.Entities.Memory>> ExtractFromEventsAsync(
        Guid projectId, Guid sessionId, IReadOnlyList<SessionEvent> events, CancellationToken ct);

    /// <summary>Extracts from the conversation summary Claude Code writes when it compacts a session.
    /// Offline (no chat provider) this yields nothing: free text has no structure a heuristic can use.</summary>
    Task<IReadOnlyList<Context.Domain.Entities.Memory>> ExtractFromCompactSummaryAsync(
        Guid projectId, Guid sessionId, string compactSummary, CancellationToken ct);
}
