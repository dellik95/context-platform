using System.Text;
using Context.Domain.Entities;

namespace Context.Memory.Extraction;

/// <summary>Builds the prompt that asks an LLM to propose candidate durable memories from one session summary chunk.</summary>
internal static class MemoryExtractionPromptBuilder
{
    public const string SystemPrompt =
        """
        You extract durable, reusable knowledge from a coding session's summary so it can be
        remembered across future sessions. Propose between 0 and 5 candidate memories — only
        include a memory if it captures something worth remembering long-term (an architectural
        decision, a non-obvious discovery about the codebase, a completed feature/bugfix/refactor
        worth recording, or an open issue). Do not restate trivia.

        Prefer fewer, higher-signal memories over many small ones. Do not split one dominant
        thread into several candidates that each restate the same fact from a different angle —
        if a single thread clearly dominates the session and everything else is trivial, propose
        one memory for it, not a handful of near-duplicates. Only propose more than one memory
        when they describe genuinely distinct decisions, discoveries, or fixes. Do not invent
        diversity where none exists.

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
        concerns. Choose FILES_READ only from the "Files read" list below, and FILES_MODIFIED only
        from the "Changed files" list below — never invent a path, and never move a path from one
        list into the other (a file that was only read is not "modified" just because this memory
        discusses it). FILES_READ is for files that were examined/relevant but not changed;
        FILES_MODIFIED is for files that were actually edited because of this memory's content. Omit
        either block entirely if it doesn't apply (most memories only need FILES_MODIFIED, or neither).

        If n is 0, respond with only "MEMORY_COUNT: 0" and nothing else.
        """;

    public static string BuildUserPrompt(SessionSummaryChunk chunk)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Session: {chunk.SessionId}");
        AppendSection(sb, "Completed", chunk.Completed);
        AppendSection(sb, "Discovered", chunk.Discovered);
        AppendSection(sb, "Decisions", chunk.Decisions);
        AppendSection(sb, "Files read", chunk.FilesReadJson);
        AppendSection(sb, "Changed files", chunk.ChangedFilesJson);
        AppendSection(sb, "Open issues", chunk.OpenIssues);
        AppendSection(sb, "Next steps", chunk.NextSteps);
        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, string label, string? content)
    {
        sb.AppendLine();
        sb.AppendLine($"{label}:");
        sb.AppendLine(string.IsNullOrWhiteSpace(content) ? "(none)" : content);
    }
}
