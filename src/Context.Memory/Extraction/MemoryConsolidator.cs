using System.Text.Json;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Providers;
using Context.Providers.Abstractions;
using Context.Providers.Embeddings;
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
    IMemoryConsolidationSettingsProvider settingsProvider,
    ILogger<MemoryConsolidator>? logger = null) : IMemoryConsolidator
{
    // Same pattern as EmbeddingLookupAdapter.InteractiveOptions: flags an interactive caller's
    // embedding call so OllamaEmbeddingGeneratorDecorator skips its concurrency gate instead of
    // queuing behind a background EmbedChunk backlog it has nothing to do with.
    private static readonly EmbeddingGenerationOptions InteractiveOptions = new()
    {
        AdditionalProperties = new AdditionalPropertiesDictionary { [OllamaEmbeddingGeneratorDecorator.InteractiveRequestKey] = true }
    };

    public async Task<ConsolidationResult> ConsolidateAsync(
        Guid projectId,
        Guid? sessionId,
        MemoryCandidate candidate,
        List<ExistingMemoryWithEmbedding> existing,
        bool isInteractive,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var settings = await settingsProvider.GetAsync(projectId, ct).ConfigureAwait(false);
        var chatClient = await providerResolver.GetChatClientAsync(projectId, ct).ConfigureAwait(false);
        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        var embeddingResult = await embeddingGenerator
            .GenerateAsync($"{candidate.Title}\n{candidate.Body}", isInteractive ? InteractiveOptions : null, ct)
            .ConfigureAwait(false);
        var embedding = embeddingResult.Vector.ToArray();

        var topMatches = FindTopMatches(embedding, existing, settings.TopKForDedup);
        var bestMatch = topMatches.Count > 0 ? topMatches[0] : ((ExistingMemoryWithEmbedding Existing, double Similarity)?)null;

        if (bestMatch is { } best0 && best0.Similarity > settings.SimilarityThreshold)
        {
            var oldMemory = best0.Existing.Memory;
            var consistent = await IsRestatementAsync(oldMemory, candidate, projectId, sessionId, chatClient, ct).ConfigureAwait(false);

            if (consistent)
            {
                await ReinforceAsync(oldMemory, now, settings.PromotionReinforcementThreshold, ct).ConfigureAwait(false);
                return new ConsolidationResult(oldMemory, ConsolidationOutcome.Reinforced);
            }

            var superseding = NewMemory(candidate, projectId, sessionId, now);
            await memoryStore.SaveAsync(superseding, embedding, ct).ConfigureAwait(false);
            await memoryStore.MarkSupersededAsync(oldMemory.Id, superseding.Id, ct).ConfigureAwait(false);

            // Dropped from the candidate set, not just replaced in the database: a later candidate
            // in this same batch would otherwise still match the superseded memory and reinforce
            // it, resurrecting the fact this one just contradicted.
            existing.RemoveAll(e => e.Memory.Id == oldMemory.Id);
            existing.Add(new ExistingMemoryWithEmbedding(superseding, embedding));
            return new ConsolidationResult(superseding, ConsolidationOutcome.Superseded);
        }

        if (bestMatch is { } best1 && best1.Similarity >= settings.LowSimilarityThreshold)
        {
            // Not similar enough to auto-reinforce outright, but close enough that it might be
            // the same fact in different words - walk the near-neighbors (nearest first) and
            // ask the same consistency checker used above; the first one judged a restatement
            // wins, so this doesn't insert a near-duplicate just because the top match's exact
            // wording happened not to match well enough to be judged the same thing.
            var restated = await FindRestatedMatchAsync(
                topMatches, candidate, projectId, sessionId, chatClient,
                settings.LowSimilarityThreshold, settings.SimilarityThreshold, ct).ConfigureAwait(false);
            if (restated is not null)
            {
                await ReinforceAsync(restated, now, settings.PromotionReinforcementThreshold, ct).ConfigureAwait(false);
                return new ConsolidationResult(restated, ConsolidationOutcome.Reinforced);
            }
        }

        var fresh = NewMemory(candidate, projectId, sessionId, now);
        await memoryStore.SaveAsync(fresh, embedding, ct).ConfigureAwait(false);
        existing.Add(new ExistingMemoryWithEmbedding(fresh, embedding));
        return new ConsolidationResult(fresh, ConsolidationOutcome.Created);
    }

    /// <summary>Checks every <paramref name="matches"/> candidate in the
    /// [<paramref name="lowSimilarityThreshold"/>, <paramref name="similarityThreshold"/>] band
    /// for a restatement judgment concurrently (each check is a synchronous LLM round-trip, and
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
        double lowSimilarityThreshold,
        double similarityThreshold,
        CancellationToken ct)
    {
        var eligible = matches
            .Where(match => match.Similarity >= lowSimilarityThreshold && match.Similarity <= similarityThreshold)
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

    private async Task ReinforceAsync(Context.Domain.Entities.Memory memory, DateTimeOffset now, int promotionReinforcementThreshold, CancellationToken ct)
    {
        var newCount = await memoryStore.ReinforceAsync(memory.Id, now, promotionReinforcementThreshold, ct).ConfigureAwait(false);
        if (newCount is null)
        {
            logger?.LogWarning("Memory {MemoryId} disappeared before it could be reinforced.", memory.Id);
            return;
        }

        // The store owns the write; this only brings the in-memory copy (returned to the caller and
        // reused as a dedup candidate for the rest of the batch) back in line with it.
        memory.ReinforcementCount = newCount.Value;
        memory.LastReinforcedAt = now;
        if (memory.Pinned || newCount.Value >= promotionReinforcementThreshold)
        {
            memory.Tier = MemoryTier.LongTerm;
        }
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
            await usageRecorder.RecordChatUsageAsync(projectId, sessionId, "MemoryConsistencyCheck", chatClient, chatResponse.Usage, logger, ct).ConfigureAwait(false);
            return MemoryConsistencyChecker.ParseIsConsistent(chatResponse.Text);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "LLM provider {Provider} failed the consistency check for memory {MemoryId}; defaulting to reinforcement.", chatClient.GetProviderName(), oldMemory.Id);
            return true; // fail open toward reinforcing rather than forking memory history
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
        ScopePath = candidate.ScopePath,
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
