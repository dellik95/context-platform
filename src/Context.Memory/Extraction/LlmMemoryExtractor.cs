using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace Context.Memory.Extraction;

/// <summary>
/// Default <see cref="IMemoryExtractor"/>. Asks an LLM (or, offline, a heuristic over the
/// summary's sections) to propose candidate memories, embeds each candidate, and deduplicates
/// it against the project's existing memories by cosine similarity:
/// <list type="bullet">
/// <item>similarity &gt; 0.92 and judged consistent with the match → reinforce the existing
/// memory (bump ReinforcementCount/LastReinforcedAt, promote to LongTerm at 2+ reinforcements
/// or if already pinned);</item>
/// <item>similarity &gt; 0.92 but judged contradictory → insert the new memory and mark the
/// old one superseded (the old memory is kept, not deleted);</item>
/// <item>no match above the threshold → insert as a new Working-tier memory.</item>
/// </list>
/// </summary>
public sealed class LlmMemoryExtractor(
    ISessionEventSource sessionEventSource,
    IMemoryStore memoryStore,
    ILlmProvider llmProvider,
    IEmbeddingProvider embeddingProvider,
    ILogger<LlmMemoryExtractor>? logger = null) : IMemoryExtractor
{
    private const double SimilarityThreshold = 0.92;
    private const int PromotionReinforcementThreshold = 2;

    public async Task<IReadOnlyList<Context.Domain.Entities.Memory>> ExtractAsync(SessionSummary summary, CancellationToken ct)
    {
        var projectId = await sessionEventSource.GetProjectIdAsync(summary.SessionId, ct).ConfigureAwait(false);

        var candidates = IsOffline(llmProvider)
            ? MemoryCandidateHeuristics.FromSummary(summary)
            : await ExtractCandidatesViaLlmAsync(summary, ct).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return [];
        }

        var existing = (await memoryStore.GetExistingWithEmbeddingsAsync(projectId, ct).ConfigureAwait(false)).ToList();
        var results = new List<Context.Domain.Entities.Memory>();
        var now = DateTimeOffset.UtcNow;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var embedding = await embeddingProvider
                .EmbedAsync($"{candidate.Title}\n{candidate.Body}", ct)
                .ConfigureAwait(false);

            var bestMatch = FindBestMatch(embedding, existing);

            if (bestMatch is { Similarity: > SimilarityThreshold })
            {
                var oldMemory = bestMatch.Value.Existing.Memory;
                var consistent = IsOffline(llmProvider)
                    ? MemoryConsistencyChecker.HeuristicIsConsistent(oldMemory, candidate)
                    : await CheckConsistencyViaLlmAsync(oldMemory, candidate, ct).ConfigureAwait(false);

                if (consistent)
                {
                    oldMemory.ReinforcementCount++;
                    oldMemory.LastReinforcedAt = now;
                    if (oldMemory.Pinned || oldMemory.ReinforcementCount >= PromotionReinforcementThreshold)
                    {
                        oldMemory.Tier = MemoryTier.LongTerm;
                    }

                    await memoryStore.SaveAsync(oldMemory, embedding: null, ct).ConfigureAwait(false);
                    results.Add(oldMemory);
                    continue;
                }

                var superseding = NewMemory(candidate, projectId, summary.SessionId, now);
                await memoryStore.SaveAsync(superseding, embedding, ct).ConfigureAwait(false);
                await memoryStore.MarkSupersededAsync(oldMemory.Id, superseding.Id, ct).ConfigureAwait(false);
                results.Add(superseding);
                existing.Add(new ExistingMemoryWithEmbedding(superseding, embedding));
                continue;
            }

            var fresh = NewMemory(candidate, projectId, summary.SessionId, now);
            await memoryStore.SaveAsync(fresh, embedding, ct).ConfigureAwait(false);
            results.Add(fresh);
            existing.Add(new ExistingMemoryWithEmbedding(fresh, embedding));
        }

        return results;
    }

    private async Task<IReadOnlyList<MemoryCandidate>> ExtractCandidatesViaLlmAsync(SessionSummary summary, CancellationToken ct)
    {
        var userPrompt = MemoryExtractionPromptBuilder.BuildUserPrompt(summary);

        string response;
        try
        {
            response = await llmProvider
                .CompleteAsync(MemoryExtractionPromptBuilder.SystemPrompt, userPrompt, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "LLM provider {Provider} failed to extract candidate memories for session {SessionId}.", llmProvider.Name, summary.SessionId);
            return [];
        }

        return MemoryCandidateParser.Parse(response);
    }

    private async Task<bool> CheckConsistencyViaLlmAsync(Context.Domain.Entities.Memory oldMemory, MemoryCandidate candidate, CancellationToken ct)
    {
        try
        {
            var response = await llmProvider
                .CompleteAsync(
                    MemoryConsistencyChecker.SystemPrompt,
                    MemoryConsistencyChecker.BuildUserPrompt(oldMemory, candidate),
                    ct)
                .ConfigureAwait(false);
            return MemoryConsistencyChecker.ParseIsConsistent(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "LLM provider {Provider} failed the consistency check for memory {MemoryId}; defaulting to reinforcement.", llmProvider.Name, oldMemory.Id);
            return true; // fail open toward reinforcing rather than forking memory history
        }
    }

    private static (ExistingMemoryWithEmbedding Existing, double Similarity)? FindBestMatch(
        float[] embedding, IReadOnlyList<ExistingMemoryWithEmbedding> existing)
    {
        ExistingMemoryWithEmbedding? best = null;
        var bestScore = double.NegativeInfinity;

        foreach (var candidate in existing)
        {
            var score = CosineSimilarity.Compute(embedding, candidate.Embedding);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best is null ? null : (best, bestScore);
    }

    private static Context.Domain.Entities.Memory NewMemory(MemoryCandidate candidate, Guid projectId, Guid sessionId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Type = candidate.Type,
        Title = candidate.Title,
        Body = candidate.Body,
        FactsJson = JsonSerializer.Serialize(candidate.Facts),
        ConceptsJson = candidate.Concepts.Count > 0 ? JsonSerializer.Serialize(candidate.Concepts) : null,
        FilesReadJson = candidate.FilesRead.Count > 0 ? JsonSerializer.Serialize(candidate.FilesRead) : null,
        FilesModifiedJson = candidate.FilesModified.Count > 0 ? JsonSerializer.Serialize(candidate.FilesModified) : null,
        Confidence = candidate.Confidence,
        Tier = MemoryTier.Working,
        Pinned = false,
        ReinforcementCount = 0,
        SupersededById = null,
        CreatedFromSessionId = sessionId,
        CreatedAt = now,
        LastReinforcedAt = now
    };

    private static bool IsOffline(ILlmProvider provider) =>
        string.Equals(provider.Name, "noop", StringComparison.OrdinalIgnoreCase);
}
