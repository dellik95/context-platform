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

        Zero memories is a normal, often CORRECT answer — a session can do real work and still
        contain nothing worth remembering. Do not manufacture a memory just to have something to
        report. Skip entirely (do not propose a memory for):
        - routine status/process narration with no durable outcome ("ran the tests", "reviewed the
          file", "checked the logs") unless it led to a concrete fix or finding
        - "tests passed" / "build succeeded" / "CI green" on their own, with no described change
        - session bookkeeping (session started, workspace loaded, context files read)
        - a task still in progress with no decision or conclusion yet

        Titles must be outcome-focused (what changed or was learned), never process-focused (what
        you did to find out). For example:
        GOOD: "OAuth2 PKCE flow added to authentication"
        GOOD: "Discovered flush strategy fails under concurrent sessions"
        BAD:  "Analyzed authentication implementation" (describes the analysis, not what was found)
        BAD:  "Reviewed session handling code" (process narration, no outcome)

        Respond in EXACTLY this format:

        MEMORY_COUNT: <n>
        MEMORY 1
        TYPE: <one of Decision, Discovery, BugFix, Feature, Refactor, Change, Exploration, Architecture, Issue>
        TITLE: <short one-line title, under 100 characters>
        BODY: <1-3 sentence explanation, self-contained without needing the session for context>
        FACTS:
        - <fact 1>
        - <fact 2>
        CONCEPTS:
        - <concept 1>
        FILES_READ:
        - <path 1>
        FILES_MODIFIED:
        - <path 1>
        CONFIDENCE: <a number between 0.0 and 1.0>
        END_MEMORY
        MEMORY 2
        ...
        END_MEMORY

        FACTS is 2-5 short, atomic, standalone facts pulled out of this memory — each one must
        make sense read on its own, with no surrounding narrative, since it is shown when a
        search result needs to say something useful in very few tokens.

        CONCEPTS is 1-3 tags naming what KIND of knowledge this memory is, chosen ONLY from this
        fixed list (do not invent new ones): how-it-works, why-it-exists, what-changed,
        problem-solution, gotcha, pattern, trade-off. This lets a future search filter by kind of
        insight instead of only matching text. Omit the CONCEPTS block entirely if none clearly apply.

        FILES_READ / FILES_MODIFIED are the specific files THIS memory is about — not every file
        touched in the whole session, only the ones this particular decision/fix/discovery
        concerns. Use exact relative paths that literally appear in the session context below; never
        invent a path. FILES_READ is for files that were examined/relevant but not changed;
        FILES_MODIFIED is for files that were actually edited because of this memory's content. Omit
        either block entirely if it doesn't apply (most memories only need FILES_MODIFIED, or neither).

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
