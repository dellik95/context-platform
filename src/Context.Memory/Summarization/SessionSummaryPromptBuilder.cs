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

        OPEN_ISSUES:
        <bullet list of unresolved problems, or "(none)">

        NEXT_STEPS:
        <bullet list of suggested follow-up work, or "(none)">

        Be terse and factual. Do not invent information that isn't supported by the events. Ground
        every bullet in the concrete specifics visible in the events — name the actual file paths,
        symbol/function/class names, commands, and error messages involved instead of describing
        them generically ("a service", "some files", "the config"); this summary is the only input
        a later memory-extraction pass sees, so a vague bullet here becomes an unusable memory later.
        """;

    public static string BuildUserPrompt(Guid sessionId, IReadOnlyList<SessionEvent> events, bool isSlice = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Session: {sessionId}");
        if (isSlice)
        {
            sb.AppendLine("This is a later slice of an ongoing session, not its start. Summarize what happened in THIS SLICE of the session, not the whole session so far.");
        }
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
