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
    internal const int MaxCandidatesPerChunk = 25;

    public async Task<IReadOnlyList<Context.Domain.Entities.Memory>> ExtractAsync(Guid projectId, SessionSummaryChunk chunk, CancellationToken ct)
    {
        var chatClient = await providerResolver.GetChatClientAsync(projectId, ct).ConfigureAwait(false);
        var usedLlm = !chatClient.IsNoOp();

        var candidates = usedLlm
            ? await ExtractCandidatesViaLlmAsync(chunk, projectId, chatClient, ct).ConfigureAwait(false)
            : MemoryCandidateHeuristics.FromSummary(chunk);

        candidates = Validate(candidates, chunk.SessionId);

        if (candidates.Count > MaxCandidatesPerChunk)
        {
            logger?.LogWarning(
                "Extraction produced {CandidateCount} candidates for session {SessionId}, above the per-chunk cap of {Cap}; the excess is dropped.",
                candidates.Count, chunk.SessionId, MaxCandidatesPerChunk);
            candidates = [.. candidates.Take(MaxCandidatesPerChunk)];
        }

        // IDs/counts only, never candidate Title/Body text (MEMORY_PIPELINE_PLAN.md §D.5/Gap #5) -
        // matches the discipline already used by this class's LogWarning calls below.
        if (candidates.Count == 0)
        {
            // A live LLM returning nothing usable is a failure worth alerting on; the offline
            // heuristic legitimately finds nothing in a thin summary.
            if (usedLlm)
            {
                logger?.LogWarning(
                    "LLM provider {Provider} produced no usable candidate memories for session {SessionId}.",
                    chatClient.GetProviderName(), chunk.SessionId);
            }
            else
            {
                logger?.LogInformation("No candidate memories for session {SessionId}.", chunk.SessionId);
            }

            return [];
        }

        logger?.LogInformation(
            "Extracted {CandidateCount} candidate memories for session {SessionId}.", candidates.Count, chunk.SessionId);

        var existing = (await memoryStore.GetExistingWithEmbeddingsAsync(projectId, ct).ConfigureAwait(false)).ToList();
        var results = new List<Context.Domain.Entities.Memory>();
        int created = 0, reinforced = 0, superseded = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var result = await consolidator.ConsolidateAsync(projectId, chunk.SessionId, candidate, existing, isInteractive: false, ct).ConfigureAwait(false);
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
            await usageRecorder.RecordChatUsageAsync(projectId, chunk.SessionId, "MemoryExtraction", chatClient, chatResponse.Usage, logger, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Rethrown, not swallowed: the job handler's retry/backoff is the only thing that can
            // recover a transient provider failure, and it can only see one that reaches it.
            logger?.LogWarning(ex, "LLM provider {Provider} failed to extract candidate memories for session {SessionId}.", chatClient.GetProviderName(), chunk.SessionId);
            throw;
        }

        var parsed = MemoryCandidateParser.Parse(response);
        if (parsed.DiscardedBlocks > 0)
        {
            logger?.LogWarning(
                "Parser discarded {DiscardedBlocks} block(s) with content but no TYPE/TITLE for session {SessionId}; provider {Provider} drifted from the requested format.",
                parsed.DiscardedBlocks, chunk.SessionId, chatClient.GetProviderName());
        }

        return parsed.Candidates;
    }

    private IReadOnlyList<MemoryCandidate> Validate(IReadOnlyList<MemoryCandidate> candidates, Guid sessionId)
    {
        var accepted = new List<MemoryCandidate>(candidates.Count);
        var rejected = new List<string>();

        foreach (var candidate in candidates)
        {
            var normalized = MemoryCandidateValidator.Normalize(candidate, out var reason);
            if (normalized is null)
            {
                rejected.Add(reason!);
                continue;
            }

            accepted.Add(normalized);
        }

        if (rejected.Count > 0)
        {
            logger?.LogWarning(
                "Rejected {RejectedCount} malformed candidate memories for session {SessionId}: {Reasons}.",
                rejected.Count, sessionId, string.Join(", ", rejected.Distinct()));
        }

        return accepted;
    }
}
