using Context.Domain.Entities;
using Context.Memory.Abstractions;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Context.Memory.Extraction;

/// <summary>
/// Default <see cref="IMemoryExtractor"/>. Asks an LLM (or, offline, a heuristic over the
/// chunk's sections) to propose candidate memories, then hands each candidate to
/// <see cref="IMemoryConsolidator"/>, which embeds it, deduplicates it against the project's
/// existing memories, and decides whether to reinforce/supersede/insert it (see
/// <see cref="MemoryConsolidator"/> for that decision logic — this class no longer makes it
/// directly; it only generates candidates and orchestrates the per-candidate consolidation call).
/// </summary>
public sealed class LlmMemoryExtractor(
    IMemoryStore memoryStore,
    IMemoryConsolidator consolidator,
    IProviderResolver providerResolver,
    IUsageRecorder usageRecorder,
    ILogger<LlmMemoryExtractor>? logger = null) : IMemoryExtractor
{
    public async Task<IReadOnlyList<Context.Domain.Entities.Memory>> ExtractAsync(Guid projectId, SessionSummaryChunk chunk, CancellationToken ct)
    {
        var chatClient = await providerResolver.GetChatClientAsync(projectId, ct).ConfigureAwait(false);

        var candidates = chatClient.IsNoOp()
            ? MemoryCandidateHeuristics.FromSummary(chunk)
            : await ExtractCandidatesViaLlmAsync(chunk, projectId, chatClient, ct).ConfigureAwait(false);

        // IDs/counts only, never candidate Title/Body text (MEMORY_PIPELINE_PLAN.md §D.5/Gap #5) -
        // matches the discipline already used by this class's LogWarning calls below.
        logger?.LogInformation(
            "Extracted {CandidateCount} candidate memories for session {SessionId}.", candidates.Count, chunk.SessionId);

        if (candidates.Count == 0)
        {
            return [];
        }

        var existing = (await memoryStore.GetExistingWithEmbeddingsAsync(projectId, ct).ConfigureAwait(false)).ToList();
        var results = new List<Context.Domain.Entities.Memory>();
        int created = 0, reinforced = 0, superseded = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var result = await consolidator.ConsolidateAsync(projectId, chunk.SessionId, candidate, existing, ct).ConfigureAwait(false);
            results.Add(result.Memory);
            switch (result.Outcome)
            {
                case ConsolidationOutcome.Created: created++; break;
                case ConsolidationOutcome.Reinforced: reinforced++; break;
                case ConsolidationOutcome.Superseded: superseded++; break;
            }
        }

        logger?.LogInformation(
            "Memory consolidation for session {SessionId}: {Created} created, {Reinforced} reinforced, {Superseded} superseded.",
            chunk.SessionId, created, reinforced, superseded);

        return results;
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

    // Best-effort: a failure recording token usage must not affect extraction, so every
    // exception here (other than cancellation) is logged and swallowed rather than propagated
    // to the caller.
    private async Task RecordUsageAsync(Guid projectId, Guid sessionId, string purpose, IChatClient chatClient, UsageDetails? usage, CancellationToken ct)
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
}
