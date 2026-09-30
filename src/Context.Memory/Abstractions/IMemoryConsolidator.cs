using Context.Memory.Extraction;

namespace Context.Memory.Abstractions;

/// <summary>
/// Places one candidate into the project's memory: relates it to its nearest neighbours and
/// reinforces, merges, supersedes, contests or inserts. Shared by
/// background extraction and explicit remember.
/// </summary>
public interface IMemoryConsolidator
{
    /// <param name="isInteractive">True for a latency-sensitive, user-facing caller (explicit remember).
    /// Flags the embedding call so <c>OllamaEmbeddingGeneratorDecorator</c> skips its concurrency gate
    /// instead of queuing behind a background embedding backlog.</param>
    /// <exception cref="ArgumentException">The candidate has no title or body, or is a non-explicit change log.</exception>
    Task<ConsolidationResult> ConsolidateAsync(
        Guid projectId,
        Guid? sessionId,
        MemoryCandidate candidate,
        bool isInteractive,
        CancellationToken ct);
}
