namespace Context.Memory.Abstractions;

/// <summary>
/// Given one candidate memory and the project's existing memories (with their embeddings),
/// decides what to do with it: reinforce an existing memory, insert it fresh, or insert it and
/// supersede an existing one. Extracted out of <c>LlmMemoryExtractor.ExtractAsync</c>'s inline
/// foreach loop (Phase 1 of the memory pipeline plan, docs/architecture/MEMORY_PIPELINE_PLAN.md
/// §D.1/§F) so the decision is unit-testable in isolation and reusable by a caller that already
/// has a ready-made candidate and does not need LLM candidate generation at all (e.g. a future
/// explicit "remember this" entry point).
/// </summary>
public interface IMemoryConsolidator
{
    /// <summary>
    /// Embeds <paramref name="candidate"/>, compares it against the nearest neighbors in
    /// <paramref name="existing"/> by cosine similarity, and reinforces/supersedes/inserts per
    /// the thresholds documented on <see cref="Context.Memory.Extraction.MemoryConsolidator"/>.
    /// When a new memory is inserted, it is appended to <paramref name="existing"/> so subsequent
    /// calls sharing the same list (e.g. later candidates in the same extraction batch) see it.
    /// </summary>
    Task<Context.Domain.Entities.Memory> ConsolidateAsync(
        Guid projectId,
        Guid? sessionId,
        Context.Memory.Extraction.MemoryCandidate candidate,
        List<ExistingMemoryWithEmbedding> existing,
        CancellationToken ct);
}
