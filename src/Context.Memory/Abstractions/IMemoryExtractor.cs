using Context.Domain.Entities;

namespace Context.Memory.Abstractions;

/// <summary>
/// Given some session material (a batch of raw events or a compaction summary),
/// asks an LLM (or falls back to a heuristic) to propose candidate memories and consolidates each
/// through <see cref="IMemoryConsolidator"/>. Every method returns the memory each candidate ended
/// up in (created, reinforced, merged, replacing or contesting another, or a recorded event).
/// </summary>
public interface IMemoryExtractor
{
    /// <summary>Extracts from one batch of raw session events, oldest first.</summary>
    Task<IReadOnlyList<Context.Domain.Entities.Memory>> ExtractFromEventsAsync(
        Guid projectId, Guid sessionId, IReadOnlyList<SessionEvent> events, CancellationToken ct);

    /// <summary>Extracts from the conversation summary Claude Code writes when it compacts a session,
    /// captured at <paramref name="observedAt"/>. Offline (no chat provider) this yields nothing: free
    /// text has no structure a heuristic can use.</summary>
    Task<IReadOnlyList<Context.Domain.Entities.Memory>> ExtractFromCompactSummaryAsync(
        Guid projectId, Guid sessionId, string compactSummary, DateTimeOffset observedAt, CancellationToken ct);
}
