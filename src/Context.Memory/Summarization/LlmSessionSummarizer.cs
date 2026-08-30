using System.Text.Json;
using Context.Domain.Entities;
using Context.Memory.Abstractions;
using Context.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace Context.Memory.Summarization;

/// <summary>
/// Default <see cref="ISessionSummarizer"/>: reads a session's events via
/// <see cref="ISessionEventSource"/> and asks an <see cref="ILlmProvider"/> to produce a
/// structured summary. When the configured provider is the offline <c>NoOpLlmProvider</c>
/// (or a real provider's response fails to parse into any recognized section), falls back
/// to a deterministic, templated summary built from event-type counts so the pipeline never
/// produces an empty summary.
/// </summary>
public sealed class LlmSessionSummarizer(
    ISessionEventSource eventSource,
    ILlmProvider llmProvider,
    ILogger<LlmSessionSummarizer>? logger = null) : ISessionSummarizer
{
    public async Task<SessionSummary> SummarizeAsync(Guid sessionId, CancellationToken ct)
    {
        var events = await eventSource.GetEventsAsync(sessionId, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        if (IsOffline(llmProvider))
        {
            return HeuristicSessionSummaryBuilder.Build(sessionId, events, now);
        }

        var userPrompt = SessionSummaryPromptBuilder.BuildUserPrompt(sessionId, events);

        string rawResponse;
        try
        {
            rawResponse = await llmProvider
                .CompleteAsync(SessionSummaryPromptBuilder.SystemPrompt, userPrompt, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "LLM provider {Provider} failed to summarize session {SessionId}; falling back to heuristic summary.", llmProvider.Name, sessionId);
            return HeuristicSessionSummaryBuilder.Build(sessionId, events, now);
        }

        var parsed = SessionSummaryParser.Parse(rawResponse);
        if (parsed.IsEffectivelyEmpty)
        {
            logger?.LogInformation("LLM response for session {SessionId} did not parse into any known section; falling back to heuristic summary.", sessionId);
            return HeuristicSessionSummaryBuilder.Build(sessionId, events, now);
        }

        return new SessionSummary
        {
            SessionId = sessionId,
            Title = DetermineTitle(parsed, sessionId),
            Completed = parsed.Completed,
            Discovered = parsed.Discovered,
            Decisions = parsed.Decisions,
            ChangedFilesJson = JsonSerializer.Serialize(parsed.ChangedFiles),
            OpenIssues = parsed.OpenIssues,
            NextSteps = parsed.NextSteps,
            GeneratedAt = now
        };
    }

    private static bool IsOffline(ILlmProvider provider) =>
        string.Equals(provider.Name, "noop", StringComparison.OrdinalIgnoreCase);

    // The LLM is asked for a TITLE section, but a response that skips it should still
    // never leave the summary with a null/empty title — fall back to something derived
    // from whatever sections did come back.
    private static string DetermineTitle(SessionSummaryParser.ParsedSections parsed, Guid sessionId)
    {
        if (!string.IsNullOrWhiteSpace(parsed.Title))
        {
            return parsed.Title.Trim();
        }

        if (parsed.ChangedFiles.Count > 0)
        {
            var first = parsed.ChangedFiles[0];
            return parsed.ChangedFiles.Count == 1
                ? $"Changes to {first}"
                : $"Changes to {first} and {parsed.ChangedFiles.Count - 1} other file(s)";
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
