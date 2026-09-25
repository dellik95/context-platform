using Context.Domain.Entities;
using Context.Memory.Abstractions;
using Context.Memory.Summarization;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Context.Memory.Extraction;

/// <summary>
/// Default <see cref="IMemoryExtractor"/>. Asks an LLM (or, offline, a heuristic over the
/// session material) to propose candidate memories, then hands each candidate to
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

    public Task<IReadOnlyList<Context.Domain.Entities.Memory>> ExtractAsync(Guid projectId, SessionSummaryChunk chunk, CancellationToken ct) =>
        ExtractCoreAsync(
            projectId,
            chunk.SessionId,
            () => MemoryExtractionPromptBuilder.BuildUserPrompt(chunk),
            () => MemoryCandidateHeuristics.FromSummary(chunk),
            ct);

    public Task<IReadOnlyList<Context.Domain.Entities.Memory>> ExtractFromEventsAsync(
        Guid projectId, Guid sessionId, IReadOnlyList<SessionEvent> events, CancellationToken ct) =>
        ExtractCoreAsync(
            projectId,
            sessionId,
            () => MemoryExtractionPromptBuilder.BuildUserPromptFromEvents(sessionId, events),
            () => MemoryCandidateHeuristics.FromSummary(HeuristicChunk(sessionId, events)),
            ct);

    public Task<IReadOnlyList<Context.Domain.Entities.Memory>> ExtractFromCompactSummaryAsync(
        Guid projectId, Guid sessionId, string compactSummary, CancellationToken ct) =>
        ExtractCoreAsync(
            projectId,
            sessionId,
            () => MemoryExtractionPromptBuilder.BuildUserPromptFromCompactSummary(sessionId, compactSummary),
            static () => [],
            ct);

    private async Task<IReadOnlyList<Context.Domain.Entities.Memory>> ExtractCoreAsync(
        Guid projectId,
        Guid sessionId,
        Func<string> buildUserPrompt,
        Func<IReadOnlyList<MemoryCandidate>> heuristicCandidates,
        CancellationToken ct)
    {
        var chatClient = await providerResolver.GetChatClientAsync(projectId, ct).ConfigureAwait(false);
        var usedLlm = !chatClient.IsNoOp();

        var candidates = usedLlm
            ? await ExtractCandidatesViaLlmAsync(buildUserPrompt(), sessionId, projectId, chatClient, ct).ConfigureAwait(false)
            : heuristicCandidates();

        candidates = Validate(candidates, sessionId);

        if (candidates.Count > MaxCandidatesPerChunk)
        {
            logger?.LogWarning(
                "Extraction produced {CandidateCount} candidates for session {SessionId}, above the per-chunk cap of {Cap}; the excess is dropped.",
                candidates.Count, sessionId, MaxCandidatesPerChunk);
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
                    chatClient.GetProviderName(), sessionId);
            }
            else
            {
                logger?.LogInformation("No candidate memories for session {SessionId}.", sessionId);
            }

            return [];
        }

        logger?.LogInformation(
            "Extracted {CandidateCount} candidate memories for session {SessionId}.", candidates.Count, sessionId);

        var existing = (await memoryStore.GetExistingWithEmbeddingsAsync(projectId, ct).ConfigureAwait(false)).ToList();
        var results = new List<Context.Domain.Entities.Memory>();
        int created = 0, reinforced = 0, superseded = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var result = await consolidator.ConsolidateAsync(projectId, sessionId, candidate, existing, isInteractive: false, ct).ConfigureAwait(false);
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
            sessionId, created, reinforced, superseded);

        return results;
    }

    private async Task<IReadOnlyList<MemoryCandidate>> ExtractCandidatesViaLlmAsync(
        string userPrompt, Guid sessionId, Guid projectId, IChatClient chatClient, CancellationToken ct)
    {
        List<ChatMessage> messages = [new(ChatRole.System, MemoryExtractionPromptBuilder.SystemPrompt), new(ChatRole.User, userPrompt)];

        string response;
        try
        {
            var chatResponse = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            response = chatResponse.Text;
            await usageRecorder.RecordChatUsageAsync(projectId, sessionId, "MemoryExtraction", chatClient, chatResponse.Usage, logger, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Rethrown, not swallowed: the job handler's retry/backoff is the only thing that can
            // recover a transient provider failure, and it can only see one that reaches it.
            logger?.LogWarning(ex, "LLM provider {Provider} failed to extract candidate memories for session {SessionId}.", chatClient.GetProviderName(), sessionId);
            throw;
        }

        var parsed = MemoryCandidateParser.Parse(response);
        if (parsed.DiscardedBlocks > 0)
        {
            logger?.LogWarning(
                "Parser discarded {DiscardedBlocks} block(s) with content but no TYPE/TITLE for session {SessionId}; provider {Provider} drifted from the requested format.",
                parsed.DiscardedBlocks, sessionId, chatClient.GetProviderName());
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

    // Offline path for raw events: the same heuristic summary the no-LLM summarizer builds, lifted
    // into a chunk so the existing section-based candidate heuristic applies unchanged.
    private static SessionSummaryChunk HeuristicChunk(Guid sessionId, IReadOnlyList<SessionEvent> events)
    {
        var summary = HeuristicSessionSummaryBuilder.Build(sessionId, events, DateTimeOffset.UtcNow);
        return new SessionSummaryChunk
        {
            SessionId = sessionId,
            EventCount = events.Count,
            Title = summary.Title,
            Completed = summary.Completed,
            Discovered = summary.Discovered,
            Decisions = summary.Decisions,
            FilesReadJson = summary.FilesReadJson,
            ChangedFilesJson = summary.ChangedFilesJson,
            OpenIssues = summary.OpenIssues,
            NextSteps = summary.NextSteps,
            GeneratedAt = summary.GeneratedAt
        };
    }
}
