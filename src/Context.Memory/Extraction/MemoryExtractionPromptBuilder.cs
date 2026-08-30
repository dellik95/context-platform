using System.Text;
using Context.Domain.Entities;

namespace Context.Memory.Extraction;

/// <summary>Builds the prompt that asks an LLM to propose candidate durable memories from a session summary.</summary>
internal static class MemoryExtractionPromptBuilder
{
    public const string SystemPrompt =
        """
        You extract durable, reusable knowledge from a coding session's summary so it can be
        remembered across future sessions. Propose between 0 and 5 candidate memories — only
        include a memory if it captures something worth remembering long-term (an architectural
        decision, a non-obvious discovery about the codebase, a completed feature/bugfix/refactor
        worth recording, or an open issue). Do not restate trivia.

        Respond in EXACTLY this format:

        MEMORY_COUNT: <n>
        MEMORY 1
        TYPE: <one of Decision, Discovery, BugFix, Feature, Refactor, Change, Exploration, Architecture, Issue>
        TITLE: <short one-line title, under 100 characters>
        BODY: <1-3 sentence explanation, self-contained without needing the session for context>
        FACTS:
        - <fact 1>
        - <fact 2>
        CONFIDENCE: <a number between 0.0 and 1.0>
        END_MEMORY
        MEMORY 2
        ...
        END_MEMORY

        FACTS is 2-5 short, atomic, standalone facts pulled out of this memory — each one must
        make sense read on its own, with no surrounding narrative, since it is shown when a
        search result needs to say something useful in very few tokens.

        If n is 0, respond with only "MEMORY_COUNT: 0" and nothing else.
        """;

    public static string BuildUserPrompt(SessionSummary summary)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Session: {summary.SessionId}");
        AppendSection(sb, "Completed", summary.Completed);
        AppendSection(sb, "Discovered", summary.Discovered);
        AppendSection(sb, "Decisions", summary.Decisions);
        AppendSection(sb, "Changed files", summary.ChangedFilesJson);
        AppendSection(sb, "Open issues", summary.OpenIssues);
        AppendSection(sb, "Next steps", summary.NextSteps);
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string label, string? content)
    {
        sb.AppendLine();
        sb.AppendLine($"{label}:");
        sb.AppendLine(string.IsNullOrWhiteSpace(content) ? "(none)" : content);
    }
}
