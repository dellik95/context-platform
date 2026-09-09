using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Context.Memory.Summarization;

/// <summary>
/// Default <see cref="ISessionSummarizer"/>: reads a session's events via
/// <see cref="ISessionEventSource"/> and asks the project's resolved <see cref="IChatClient"/>
/// (via <see cref="IProviderResolver"/>) to produce a structured summary. When the resolved client
/// is the offline <c>NoOpChatClient</c> (or a real provider's response fails to parse into any
/// recognized section), falls back to a deterministic, templated summary built from event-type
/// counts so the pipeline never produces an empty summary.
/// </summary>
public sealed class LlmSessionSummarizer(
    ISessionEventSource eventSource,
    IProviderResolver providerResolver,
    IUsageRecorder usageRecorder,
    ILogger<LlmSessionSummarizer>? logger = null) : ISessionSummarizer
{
    public async Task<SessionSummaryChunk> SummarizeAsync(Guid projectId, Guid sessionId, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var chatClient = await providerResolver.GetChatClientAsync(projectId, ct).ConfigureAwait(false);
        var allEvents = await eventSource.GetEventsAsync(projectId, sessionId, ct).ConfigureAwait(false);
        List<SessionEvent> events = since is null
            ? [.. allEvents]
            : [.. allEvents.Where(e => e.Timestamp > since.Value)];

        var now = DateTimeOffset.UtcNow;
        // Empty slice (no events landed since the last chunk): the chunk still has to carry a
        // valid range, so it collapses to a zero-width range at "since" (or "now" for a brand
        // new, empty session).
        var fromEventAt = events.Count > 0 ? events[0].Timestamp : since ?? now;
        var toEventAt = events.Count > 0 ? events[^1].Timestamp : since ?? now;

        if (chatClient.IsNoOp())
        {
            return ToChunk(HeuristicSessionSummaryBuilder.Build(sessionId, events, now), fromEventAt, toEventAt, events.Count);
        }

        var session = await eventSource.GetSessionAsync(projectId, sessionId, ct).ConfigureAwait(false);
        var userPrompt = SessionSummaryPromptBuilder.BuildUserPrompt(sessionId, events, isSlice: since is not null, session: session);
        List<ChatMessage> messages = [new(ChatRole.System, SessionSummaryPromptBuilder.SystemPrompt), new(ChatRole.User, userPrompt)];

        string rawResponse;
        try
        {
            var chatResponse = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            rawResponse = chatResponse.Text;
            await RecordUsageAsync(projectId, sessionId, chatClient, chatResponse.Usage, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "LLM provider {Provider} failed to summarize session {SessionId}; falling back to heuristic summary.", chatClient.GetProviderName(), sessionId);
            return ToChunk(HeuristicSessionSummaryBuilder.Build(sessionId, events, now), fromEventAt, toEventAt, events.Count);
        }

        var parsed = SessionSummaryParser.Parse(rawResponse);
        if (parsed.IsEffectivelyEmpty)
        {
            logger?.LogInformation("LLM response for session {SessionId} did not parse into any known section; falling back to heuristic summary.", sessionId);
            return ToChunk(HeuristicSessionSummaryBuilder.Build(sessionId, events, now), fromEventAt, toEventAt, events.Count);
        }

        var editedFiles = PayloadFieldExtractor.ExtractDistinctPaths(events, SessionEventType.FileEdit);
        var readFiles = PayloadFieldExtractor.ExtractDistinctPaths(events, SessionEventType.FileRead);

        return new SessionSummaryChunk
        {
            SessionId = sessionId,
            FromEventAt = fromEventAt,
            ToEventAt = toEventAt,
            EventCount = events.Count,
            Title = DetermineTitle(parsed, editedFiles, sessionId),
            Completed = parsed.Completed,
            Discovered = parsed.Discovered,
            Decisions = parsed.Decisions,
            FilesReadJson = JsonSerializer.Serialize(readFiles),
            ChangedFilesJson = JsonSerializer.Serialize(editedFiles),
            OpenIssues = parsed.OpenIssues,
            NextSteps = parsed.NextSteps,
            GeneratedAt = now
        };
    }

    // Best-effort: a failure recording token usage must not affect the summary already produced,
    // so every exception here (other than cancellation) is logged and swallowed rather than
    // propagated to the caller.
    private async Task RecordUsageAsync(Guid projectId, Guid sessionId, IChatClient chatClient, UsageDetails? usage, CancellationToken ct)
    {
        try
        {
            await usageRecorder.RecordAsync(new UsageRecord
            {
                ProjectId = projectId,
                SessionId = sessionId,
                Purpose = "SessionSummary",
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
            logger?.LogWarning(ex, "Failed to record token usage for session {SessionId}.", sessionId);
        }
    }

    // HeuristicSessionSummaryBuilder still returns the flat SessionSummary shape (Context.Memory's
    // own no-LLM fallback, unrelated to chunking) - lift its fields into a chunk alongside the
    // range/count info SummarizeAsync computed for this call.
    private static SessionSummaryChunk ToChunk(SessionSummary summary, DateTimeOffset fromEventAt, DateTimeOffset toEventAt, int eventCount) => new()
    {
        SessionId = summary.SessionId,
        FromEventAt = fromEventAt,
        ToEventAt = toEventAt,
        EventCount = eventCount,
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

    // The LLM is asked for a TITLE section, but a response that skips it should still
    // never leave the summary with a null/empty title — fall back to something derived
    // from whatever sections did come back. Uses the deterministic edited-files list (from
    // events), not LLM prose, so the fallback title can't name a file that wasn't touched.
    private static string DetermineTitle(SessionSummaryParser.ParsedSections parsed, List<string> editedFiles, Guid sessionId)
    {
        if (!string.IsNullOrWhiteSpace(parsed.Title))
        {
            return parsed.Title.Trim();
        }

        if (editedFiles.Count > 0)
        {
            var first = editedFiles[0];
            return editedFiles.Count == 1
                ? $"Changes to {first}"
                : $"Changes to {first} and {editedFiles.Count - 1} other file(s)";
        }

        if (!string.IsNullOrWhiteSpace(parsed.Completed))
        {
            return FirstLine(parsed.Completed);
        }

        return $"Session {sessionId}";
    }

    private static string FirstLine(string text)
    {
        var line = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? text;
        return line.TrimStart('-', '*', ' ');
    }
}
