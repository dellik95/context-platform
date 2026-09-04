using Context.Domain.Entities;

namespace Context.Memory.Abstractions;

/// <summary>
/// Given one session summary chunk, asks an LLM (or falls back to a heuristic) to propose
/// candidate durable memories, embeds and deduplicates each candidate against the project's
/// existing memories, and writes the result (new memories, reinforcements, and supersessions) via
/// <see cref="IMemoryStore"/>.
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
}
