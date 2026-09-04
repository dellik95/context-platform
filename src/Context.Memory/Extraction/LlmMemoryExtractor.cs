using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Context.Memory.Extraction;

/// <summary>
/// Default <see cref="IMemoryExtractor"/>. Asks an LLM (or, offline, a heuristic over the
/// chunk's sections) to propose candidate memories, embeds each candidate, and deduplicates
/// it against the top <see cref="TopKForDedup"/> nearest existing memories by cosine similarity:
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
/// </summary>
public sealed class LlmMemoryExtractor(
    IMemoryStore memoryStore,
    IProviderResolver providerResolver,
    IUsageRecorder usageRecorder,
    ILogger<LlmMemoryExtractor>? logger = null) : IMemoryExtractor
{
    private const double SimilarityThreshold = 0.92;
    private const double LowSimilarityThreshold = 0.80;
    private const int PromotionReinforcementThreshold = 2;
    private const int TopKForDedup = 3;

    public async Task<IReadOnlyList<Context.Domain.Entities.Memory>> ExtractAsync(Guid projectId, SessionSummaryChunk chunk, CancellationToken ct)
    {
        var chatClient = await providerResolver.GetChatClientAsync(projectId, ct).ConfigureAwait(false);
        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);

        var candidates = chatClient.IsNoOp()
            ? MemoryCandidateHeuristics.FromSummary(chunk)
            : await ExtractCandidatesViaLlmAsync(chunk, projectId, chatClient, ct).ConfigureAwait(false);

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

            var embeddingResult = await embeddingGenerator
                .GenerateAsync($"{candidate.Title}\n{candidate.Body}", cancellationToken: ct)
                .ConfigureAwait(false);
            var embedding = embeddingResult.Vector.ToArray();

            var topMatches = FindTopMatches(embedding, existing, TopKForDedup);
            var bestMatch = topMatches.Count > 0 ? topMatches[0] : ((ExistingMemoryWithEmbedding Existing, double Similarity)?)null;

            if (bestMatch is { Similarity: > SimilarityThreshold })
            {
                var oldMemory = bestMatch.Value.Existing.Memory;
                var consistent = await IsRestatementAsync(oldMemory, candidate, projectId, chunk.SessionId, chatClient, ct).ConfigureAwait(false);

                if (consistent)
                {
                    await ReinforceAsync(oldMemory, now, ct).ConfigureAwait(false);
                    results.Add(oldMemory);
                    continue;
                }

                var superseding = NewMemory(candidate, projectId, chunk.SessionId, now);
                await memoryStore.SaveAsync(superseding, embedding, ct).ConfigureAwait(false);
                await memoryStore.MarkSupersededAsync(oldMemory.Id, superseding.Id, ct).ConfigureAwait(false);
                results.Add(superseding);
                existing.Add(new ExistingMemoryWithEmbedding(superseding, embedding));
                continue;
            }

            if (bestMatch is { Similarity: >= LowSimilarityThreshold })
            {
                // Not similar enough to auto-reinforce outright, but close enough that it might be
                // the same fact in different words - walk the near-neighbors (nearest first) and
                // ask the same consistency checker used above; the first one judged a restatement
                // wins, so this doesn't insert a near-duplicate just because the top match's exact
                // wording happened not to match well enough to be judged the same thing.
                var restated = await FindRestatedMatchAsync(topMatches, candidate, projectId, chunk.SessionId, chatClient, ct).ConfigureAwait(false);
                if (restated is not null)
                {
                    await ReinforceAsync(restated, now, ct).ConfigureAwait(false);
                    results.Add(restated);
                    continue;
                }
            }

            var fresh = NewMemory(candidate, projectId, chunk.SessionId, now);
            await memoryStore.SaveAsync(fresh, embedding, ct).ConfigureAwait(false);
            results.Add(fresh);
            existing.Add(new ExistingMemoryWithEmbedding(fresh, embedding));
        }

        return results;
    }

    /// <summary>Walks <paramref name="matches"/> nearest-first, checking each candidate in the
    /// [<see cref="LowSimilarityThreshold"/>, <see cref="SimilarityThreshold"/>] band for a
    /// restatement judgment, and returns the first one found (or null if none qualify).</summary>
    private async Task<Context.Domain.Entities.Memory?> FindRestatedMatchAsync(
        IReadOnlyList<(ExistingMemoryWithEmbedding Existing, double Similarity)> matches,
        MemoryCandidate candidate,
        Guid projectId,
        Guid sessionId,
        IChatClient chatClient,
        CancellationToken ct)
    {
        foreach (var match in matches)
        {
            if (match.Similarity < LowSimilarityThreshold || match.Similarity > SimilarityThreshold)
            {
                continue;
            }

            var oldMemory = match.Existing.Memory;
            if (await IsRestatementAsync(oldMemory, candidate, projectId, sessionId, chatClient, ct).ConfigureAwait(false))
            {
                return oldMemory;
            }
        }

        return null;
    }

    private Task<bool> IsRestatementAsync(
        Context.Domain.Entities.Memory oldMemory, MemoryCandidate candidate, Guid projectId, Guid sessionId, IChatClient chatClient, CancellationToken ct) =>
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

    private async Task<IReadOnlyList<MemoryCandidate>> ExtractCandidatesViaLlmAsync(SessionSummaryChunk chunk, Guid projectId, IChatClient chatClient, CancellationToken ct)
    {
        var userPrompt = MemoryExtractionPromptBuilder.BuildUserPrompt(chunk);
        List<ChatMessage> messages = [new(ChatRole.System, MemoryExtractionPromptBuilder.SystemPrompt), new(ChatRole.User, userPrompt)];

        string response;
        try
        {
            var chatResponse = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            response = chatResponse.Text;
            await RecordUsageAsync(projectId, chunk.SessionId, "MemoryExtraction", chatClient, chatResponse.Usage, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "LLM provider {Provider} failed to extract candidate memories for session {SessionId}.", chatClient.GetProviderName(), chunk.SessionId);
            return [];
        }

        return MemoryCandidateParser.Parse(response);
    }

    private async Task<bool> CheckConsistencyViaLlmAsync(
        Context.Domain.Entities.Memory oldMemory, MemoryCandidate candidate, Guid projectId, Guid sessionId, IChatClient chatClient, CancellationToken ct)
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

    // Best-effort: a failure recording token usage must not affect extraction/consistency
    // checking, so every exception here (other than cancellation) is logged and swallowed
    // rather than propagated to the caller.
    private async Task RecordUsageAsync(Guid projectId, Guid sessionId, string purpose, IChatClient chatClient, UsageDetails? usage, CancellationToken ct)
    {
        try
        {
            await usageRecorder.RecordAsync(new UsageRecord
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
}
