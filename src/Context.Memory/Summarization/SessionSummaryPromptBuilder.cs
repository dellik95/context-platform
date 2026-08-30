using System.Text;
using Context.Domain.Entities;

namespace Context.Memory.Summarization;

/// <summary>Builds the system/user prompt asking an LLM to summarize a session's events.</summary>
internal static class SessionSummaryPromptBuilder
{
    private const int MaxEvents = 400;
    private const int MaxPayloadPreview = 240;

    public const string SystemPrompt =
        """
        You are a senior engineer writing a concise end-of-session handoff note for a coding
        assistant's memory system. You will be given a chronological list of session events
        (prompts, tool calls, file reads/edits, shell commands, errors). Summarize them into
        EXACTLY the following sections, using this exact format (a section may be empty but
        must still be present with its header):

        TITLE:
        <a single short, specific, information-dense line naming what this session did, written
        like a git commit subject line — concrete, not generic filler such as "Session Summary"
        or "Development Session">

        COMPLETED:
        <bullet list of what was accomplished, or "(none)">

        DISCOVERED:
        <bullet list of notable facts learned about the codebase, or "(none)">

        DECISIONS:
        <bullet list of decisions made and why, or "(none)">

        CHANGED_FILES:
        <one relative file path per line, or "(none)">

        OPEN_ISSUES:
        <bullet list of unresolved problems, or "(none)">

        NEXT_STEPS:
        <bullet list of suggested follow-up work, or "(none)">

        Be terse and factual. Do not invent information that isn't supported by the events.
        """;

    public static string BuildUserPrompt(Guid sessionId, IReadOnlyList<SessionEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Session: {sessionId}");
        sb.AppendLine($"Total events: {events.Count}");
        sb.AppendLine();

        var relevant = events.Count > MaxEvents
            ? events.Skip(events.Count - MaxEvents).ToList()
            : events;

        if (relevant.Count < events.Count)
        {
            sb.AppendLine($"(showing the most recent {relevant.Count} of {events.Count} events)");
        }

        foreach (var evt in relevant)
        {
            sb.Append('[').Append(evt.Timestamp.UtcDateTime.ToString("O")).Append("] ");
            sb.Append(evt.Type).Append(": ");
            sb.AppendLine(SummarizePayload(evt));
        }

        return sb.ToString();
    }

    private static string SummarizePayload(SessionEvent evt)
    {
        var path = PayloadFieldExtractor.TryExtractPath(evt.PayloadJson);
        var preview = Truncate(evt.PayloadJson, MaxPayloadPreview);
        return path is null ? preview : $"{path} — {preview}";
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(empty)";
        }

        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}
