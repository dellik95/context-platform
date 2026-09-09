using System.Text.Json;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Context.Memory.Extraction;

/// <summary>
/// Default <see cref="IMemoryConsolidator"/>. Moved verbatim out of
/// <see cref="LlmMemoryExtractor"/>'s ExtractAsync foreach loop (Phase 1 of the memory pipeline
/// plan, docs/architecture/MEMORY_PIPELINE_PLAN.md §D.1/§F) — same decision logic, same
/// thresholds, no behavior change:
/// <list type="bullet">
/// <item>best-match similarity &gt; 0.92 and judged consistent with that match → reinforce the
/// existing memory (bump ReinforcementCount/LastReinforcedAt, promote to LongTerm at 2+
/// reinforcements or if already pinned);</item>
/// <item>best-match similarity &gt; 0.92 but judged contradictory → insert the new memory and
/// mark the old one superseded (the old memory is kept, not deleted);</item>
/// <item>best-match similarity in [0.80, 0.92] → walk the near-neighbors (nearest first) through
/// the same consistency check; the first one judged a reworded restatement is reinforced instead
/// of inserting a near-duplicate, otherwise the candidate is genuinely new and gets inserted
/// (without superseding anything — nothing in this band was judged to contradict it);</item>
/// <item>no match at or above 0.80 → insert as a new Working-tier memory.</item>
/// </list>
/// Retry-safety note (MEMORY_PIPELINE_PLAN.md §D.6/Gap #6): the &gt;0.92 reinforce path is why
/// reprocessing the same chunk twice doesn't duplicate memories — but that's a side effect of the
/// similarity threshold, not a designed idempotency guarantee. Don't tighten these thresholds
/// without checking whether anything still relies on this.
/// </summary>
public sealed class MemoryConsolidator(
    IMemoryStore memoryStore,
    IProviderResolver providerResolver,
    IUsageRecorder usageRecorder,
    ILogger<MemoryConsolidator>? logger = null) : IMemoryConsolidator
{
    private const double SimilarityThreshold = 0.92;
    private const double LowSimilarityThreshold = 0.80;
    private const int PromotionReinforcementThreshold = 2;
    private const int TopKForDedup = 3;

    public async Task<ConsolidationResult> ConsolidateAsync(
        Guid projectId,
        Guid? sessionId,
        MemoryCandidate candidate,
        List<ExistingMemoryWithEmbedding> existing,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var chatClient = await providerResolver.GetChatClientAsync(projectId, ct).ConfigureAwait(false);
        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        var embeddingResult = await embeddingGenerator
            .GenerateAsync($"{candidate.Title}\n{candidate.Body}", cancellationToken: ct)
            .ConfigureAwait(false);
        var embedding = embeddingResult.Vector.ToArray();

        var topMatches = FindTopMatches(embedding, existing, TopKForDedup);
        var bestMatch = topMatches.Count > 0 ? topMatches[0] : ((ExistingMemoryWithEmbedding Existing, double Similarity)?)null;

        if (bestMatch is { Similarity: > SimilarityThreshold })
        {
            var oldMemory = bestMatch.Value.Existing.Memory;
            var consistent = await IsRestatementAsync(oldMemory, candidate, projectId, sessionId, chatClient, ct).ConfigureAwait(false);

            if (consistent)
            {
                await ReinforceAsync(oldMemory, now, ct).ConfigureAwait(false);
                return new ConsolidationResult(oldMemory, ConsolidationOutcome.Reinforced);
            }

            var superseding = NewMemory(candidate, projectId, sessionId, now);
            await memoryStore.SaveAsync(superseding, embedding, ct).ConfigureAwait(false);
            await memoryStore.MarkSupersededAsync(oldMemory.Id, superseding.Id, ct).ConfigureAwait(false);
            existing.Add(new ExistingMemoryWithEmbedding(superseding, embedding));
            return new ConsolidationResult(superseding, ConsolidationOutcome.Superseded);
        }

        if (bestMatch is { Similarity: >= LowSimilarityThreshold })
        {
            // Not similar enough to auto-reinforce outright, but close enough that it might be
            // the same fact in different words - walk the near-neighbors (nearest first) and
            // ask the same consistency checker used above; the first one judged a restatement
            // wins, so this doesn't insert a near-duplicate just because the top match's exact
            // wording happened not to match well enough to be judged the same thing.
            var restated = await FindRestatedMatchAsync(topMatches, candidate, projectId, sessionId, chatClient, ct).ConfigureAwait(false);
            if (restated is not null)
            {
                await ReinforceAsync(restated, now, ct).ConfigureAwait(false);
                return new ConsolidationResult(restated, ConsolidationOutcome.Reinforced);
            }
        }

        var fresh = NewMemory(candidate, projectId, sessionId, now);
        await memoryStore.SaveAsync(fresh, embedding, ct).ConfigureAwait(false);
        existing.Add(new ExistingMemoryWithEmbedding(fresh, embedding));
        return new ConsolidationResult(fresh, ConsolidationOutcome.Created);
    }

    /// <summary>Checks every <paramref name="matches"/> candidate in the
    /// [<see cref="LowSimilarityThreshold"/>, <see cref="SimilarityThreshold"/>] band for a
    /// restatement judgment concurrently (each check is a synchronous LLM round-trip, and
    /// TopKForDedup is small/fixed, so firing them together instead of one-by-one materially cuts
    /// memory_remember's latency), then returns the nearest one judged a restatement — same
    /// winner <c>matches</c>' nearest-first sequential walk would have picked, just without paying
    /// for each round-trip serially.</summary>
    private async Task<Context.Domain.Entities.Memory?> FindRestatedMatchAsync(
        IReadOnlyList<(ExistingMemoryWithEmbedding Existing, double Similarity)> matches,
        MemoryCandidate candidate,
        Guid projectId,
        Guid? sessionId,
        IChatClient chatClient,
        CancellationToken ct)
    {
        var eligible = matches
            .Where(match => match.Similarity >= LowSimilarityThreshold && match.Similarity <= SimilarityThreshold)
            .ToList();

        var verdicts = await Task.WhenAll(
            eligible.Select(match => IsRestatementAsync(match.Existing.Memory, candidate, projectId, sessionId, chatClient, ct)))
            .ConfigureAwait(false);

        for (var i = 0; i < eligible.Count; i++)
        {
            if (verdicts[i])
            {
                return eligible[i].Existing.Memory;
            }
        }

        return null;
    }

    private Task<bool> IsRestatementAsync(
        Context.Domain.Entities.Memory oldMemory, MemoryCandidate candidate, Guid projectId, Guid? sessionId, IChatClient chatClient, CancellationToken ct) =>
        chatClient.IsNoOp()
            ? Task.FromResult(MemoryConsistencyChecker.HeuristicIsConsistent(oldMemory, candidate))
            : CheckConsistencyViaLlmAsync(oldMemory, candidate, projectId, sessionId, chatClient, ct);

    private async Task ReinforceAsync(Context.Domain.Entities.Memory memory, DateTimeOffset now, CancellationToken ct)
    {
        memory.ReinforcementCount++;
        memory.LastReinforcedAt = now;
        if (memory.Pinned || memory.ReinforcementCount >= PromotionReinforcementThreshold)
        {
            memory.Tier = MemoryTier.LongTerm;
        }

        await memoryStore.SaveAsync(memory, embedding: null, ct).ConfigureAwait(false);
    }

    private async Task<bool> CheckConsistencyViaLlmAsync(
        Context.Domain.Entities.Memory oldMemory, MemoryCandidate candidate, Guid projectId, Guid? sessionId, IChatClient chatClient, CancellationToken ct)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, MemoryConsistencyChecker.SystemPrompt),
            new(ChatRole.User, MemoryConsistencyChecker.BuildUserPrompt(oldMemory, candidate)),
        ];

        try
        {
            var chatResponse = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            await RecordUsageAsync(projectId, sessionId, "MemoryConsistencyCheck", chatClient, chatResponse.Usage, ct).ConfigureAwait(false);
            return MemoryConsistencyChecker.ParseIsConsistent(chatResponse.Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "LLM provider {Provider} failed the consistency check for memory {MemoryId}; defaulting to reinforcement.", chatClient.GetProviderName(), oldMemory.Id);
            return true; // fail open toward reinforcing rather than forking memory history
        }
    }

    // Best-effort: a failure recording token usage must not affect the consistency check, so
    // every exception here (other than cancellation) is logged and swallowed rather than
    // propagated to the caller.
    private async Task RecordUsageAsync(Guid projectId, Guid? sessionId, string purpose, IChatClient chatClient, UsageDetails? usage, CancellationToken ct)
    {
        try
        {
            await usageRecorder.RecordAsync(new Context.Domain.Entities.UsageRecord
            {
                ProjectId = projectId,
                SessionId = sessionId,
                Purpose = purpose,
                Provider = chatClient.GetProviderName(),
                Model = chatClient.GetModelId(),
                PromptTokens = (int?)usage?.InputTokenCount,
                CompletionTokens = (int?)usage?.OutputTokenCount,
                TotalTokens = (int?)usage?.TotalTokenCount,
                CreatedAt = DateTimeOffset.UtcNow
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Failed to record token usage (purpose={Purpose}) for session {SessionId}.", purpose, sessionId);
        }
    }

    /// <summary>The <paramref name="count"/> existing memories nearest <paramref name="embedding"/>
    /// by cosine similarity, descending (index 0 is the single nearest neighbor).</summary>
    private static IReadOnlyList<(ExistingMemoryWithEmbedding Existing, double Similarity)> FindTopMatches(
        float[] embedding, IReadOnlyList<ExistingMemoryWithEmbedding> existing, int count) =>
        existing
            .Select(candidate => (Existing: candidate, Similarity: CosineSimilarity.Compute(embedding, candidate.Embedding)))
            .OrderByDescending(match => match.Similarity)
            .Take(count)
            .ToList();

    private static Context.Domain.Entities.Memory NewMemory(MemoryCandidate candidate, Guid projectId, Guid? sessionId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Type = candidate.Type,
        Title = candidate.Title,
        Body = candidate.Body,
        Topic = candidate.Topic,
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
}
