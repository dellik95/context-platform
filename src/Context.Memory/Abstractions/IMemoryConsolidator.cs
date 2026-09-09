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
    /// The returned <see cref="ConsolidationResult.Outcome"/> tells the caller which branch fired,
    /// for structured observability (MEMORY_PIPELINE_PLAN.md §D.5/Gap #5) — a superseding insert
    /// and a fresh insert both return "a new Memory row" and are otherwise indistinguishable from
    /// the outside.
    /// </summary>
    /// <param name="isInteractive">
    /// True for a latency-sensitive, user-facing caller (<c>ContextEngine.RememberExplicitlyAsync</c>,
    /// the <c>memory_remember</c> MCP tool) rather than periodic/background extraction
    /// (<c>LlmMemoryExtractor</c>). Flags the embedding call the same way
    /// <c>EmbeddingLookupAdapter.EmbedAsync</c> already flags live search queries, so it skips
    /// <c>OllamaEmbeddingGeneratorDecorator</c>'s concurrency gate instead of queuing behind a
    /// background EmbedChunk backlog that can hold the gate for minutes.
    /// </param>
    Task<ConsolidationResult> ConsolidateAsync(
        Guid projectId,
        Guid? sessionId,
        Context.Memory.Extraction.MemoryCandidate candidate,
        List<ExistingMemoryWithEmbedding> existing,
        bool isInteractive,
        CancellationToken ct);
}
