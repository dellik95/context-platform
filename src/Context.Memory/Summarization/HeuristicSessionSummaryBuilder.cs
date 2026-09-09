using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Memory.Summarization;

/// <summary>
/// Builds a <see cref="SessionSummary"/> purely from simple pattern matching over payloads — no
/// LLM call. Used when no LLM provider is configured (<c>NoOpLlmProvider</c>) and as a safety-net
/// fallback if a real LLM's response doesn't parse into any of the expected sections. Only reports
/// what's actually informative (files touched, errors); raw event-type counts aren't a useful
/// summary on their own, so a session with nothing noteworthy gets a null/minimal result rather
/// than a manufactured "N prompt(s), N tool call(s)..." line.
/// </summary>
internal static class HeuristicSessionSummaryBuilder
{
    public static SessionSummary Build(Guid sessionId, IReadOnlyList<SessionEvent> events, DateTimeOffset now)
    {
        var editedFiles = PayloadFieldExtractor.ExtractDistinctPaths(events, SessionEventType.FileEdit);
        var readFiles = PayloadFieldExtractor.ExtractDistinctPaths(events, SessionEventType.FileRead);
        var errors = events.Where(e => e.Type == SessionEventType.Error).ToList();

        var completed = BulletOrNone(
        [
            editedFiles.Count > 0 ? $"{editedFiles.Count} file(s) edited" : null,
            readFiles.Count > 0 ? $"{readFiles.Count} file(s) read for context" : null
        ]);

        var openIssues = errors.Count == 0
            ? null
            : $"- {errors.Count} error event(s) occurred during the session (see session events for detail)";

        var filesReadJson = JsonSerializer.Serialize(readFiles);
        var changedFilesJson = JsonSerializer.Serialize(editedFiles);
        var title = DeriveTitle(editedFiles, readFiles, events.Count);

        return new SessionSummary
        {
            SessionId = sessionId,
            Title = title,
            Completed = completed,
            Discovered = null, // no LLM available to infer facts from raw events
            Decisions = null, // no LLM available to infer rationale from raw events
            FilesReadJson = filesReadJson,
            ChangedFilesJson = changedFilesJson,
            OpenIssues = openIssues,
            NextSteps = events.Count == 0
                ? "No activity recorded for this session."
                : "Review changed files and continue outstanding work; configure an LLM provider for richer summaries.",
            GeneratedAt = now
        };
    }

    private static string DeriveTitle(List<string> editedFiles, List<string> readFiles, int eventCount)
    {
        if (editedFiles.Count > 0)
        {
            return editedFiles.Count == 1
                ? $"Edited {editedFiles[0]}"
                : $"Edited {editedFiles[0]} and {editedFiles.Count - 1} other file(s)";
        }

        if (readFiles.Count > 0)
        {
            return readFiles.Count == 1
                ? $"Read {readFiles[0]}"
                : $"Read {readFiles[0]} and {readFiles.Count - 1} other file(s)";
        }

        return eventCount == 0 ? "No activity recorded" : "Session with no file changes";
    }

    private static string? BulletOrNone(IEnumerable<string?> lines)
    {
        var present = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToList();
        return present.Count == 0 ? null : string.Join('\n', present.Select(l => $"- {l}"));
    }
}
