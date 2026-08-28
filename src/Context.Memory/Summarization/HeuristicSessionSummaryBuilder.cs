using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Memory.Summarization;

/// <summary>
/// Builds a templated, non-empty <see cref="SessionSummary"/> purely from event-type counts
/// and simple pattern matching over payloads — no LLM call. Used when no LLM provider is
/// configured (<c>NoOpLlmProvider</c>) and as a safety-net fallback if a real LLM's response
/// doesn't parse into any of the expected sections.
/// </summary>
internal static class HeuristicSessionSummaryBuilder
{
    public static SessionSummary Build(Guid sessionId, IReadOnlyList<SessionEvent> events, DateTimeOffset now)
    {
        var counts = events
            .GroupBy(e => e.Type)
            .ToDictionary(g => g.Key, g => g.Count());

        var editedFiles = ExtractDistinctPaths(events, SessionEventType.FileEdit);
        var readFiles = ExtractDistinctPaths(events, SessionEventType.FileRead);
        var errors = events.Where(e => e.Type == SessionEventType.Error).ToList();
        var commands = counts.GetValueOrDefault(SessionEventType.Command);
        var toolCalls = counts.GetValueOrDefault(SessionEventType.ToolCall);
        var prompts = counts.GetValueOrDefault(SessionEventType.Prompt);

        var completed = events.Count == 0
            ? null
            : BulletOrNone(
            [
                $"{prompts} prompt(s), {toolCalls} tool call(s) and {commands} command(s) processed",
                editedFiles.Count > 0 ? $"{editedFiles.Count} file(s) edited" : null
            ]);

        var discovered = readFiles.Count == 0
            ? null
            : $"- {readFiles.Count} file(s) were read during the session:\n" +
              string.Join('\n', readFiles.Take(20).Select(f => $"  - {f}"));

        var openIssues = errors.Count == 0
            ? null
            : $"- {errors.Count} error event(s) occurred during the session (see session events for detail)";

        var changedFilesJson = JsonSerializer.Serialize(editedFiles);

        return new SessionSummary
        {
            SessionId = sessionId,
            Completed = completed,
            Discovered = discovered,
            Decisions = null, // no LLM available to infer rationale from raw events
            ChangedFilesJson = changedFilesJson,
            OpenIssues = openIssues,
            NextSteps = events.Count == 0
                ? "No activity recorded for this session."
                : "Review changed files and continue outstanding work; configure an LLM provider for richer summaries.",
            GeneratedAt = now
        };
    }

    private static List<string> ExtractDistinctPaths(IReadOnlyList<SessionEvent> events, SessionEventType type)
    {
        return events
            .Where(e => e.Type == type)
            .Select(e => PayloadFieldExtractor.TryExtractPath(e.PayloadJson))
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct()
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? BulletOrNone(IEnumerable<string?> lines)
    {
        var present = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return present.Count == 0 ? null : string.Join('\n', present.Select(l => $"- {l}"));
    }
}
