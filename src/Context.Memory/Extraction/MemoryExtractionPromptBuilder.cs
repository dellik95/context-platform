using System.Text;
using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Summarization;

namespace Context.Memory.Extraction;

/// <summary>Builds the prompt that asks an LLM to propose candidate durable memories from one piece
/// of session material: a batch of raw session events or a compaction summary.</summary>
internal static class MemoryExtractionPromptBuilder
{
    private const int MaxPromptChars = 2000;
    private const int MaxAssistantMessageChars = 3000;
    private const int MaxToolInputChars = 1000;
    private const int MaxToolResponseChars = 600;
    private const int MaxOtherPayloadChars = 600;
    private const int MaxEventsSectionChars = 60_000;
    private const int MaxCompactSummaryChars = 60_000;

    public const string SystemPrompt =
        """
        You extract durable, reusable ENGINEERING KNOWLEDGE about this repository from coding-session
        material — a batch of raw session events (user prompts, the assistant's replies, tool calls
        with their inputs and outputs, file reads/edits, shell commands, errors), a conversation summary written when the session
        was compacted, or a session summary — so it can be remembered across future sessions. Memory is a long-term
        engineering knowledge base for this codebase — not a transcript of what happened in this
        session. Propose between 0 and 5 candidate memories.

        CORE RULE: only propose a memory if it passes this test — "would this still help another
        coding session, working on this repository weeks or months from now, understand, modify,
        debug, test, or navigate the project?" If no, reject it, no matter how notable it seemed
        in the moment.

        Zero memories is a normal, often CORRECT answer — a session can do real work and still
        contain nothing worth remembering. Do not manufacture a memory just to have something to
        report.

        Reject (do NOT propose a memory for) anything that is session/process narration rather
        than project knowledge:
        - what was searched, grepped, or inspected, and in what order ("checked X", "searched for
          Y", "traced the error through four hops") — keep the verified conclusion, if any, drop
          the process that led to it
        - routine status narration with no durable outcome ("ran the tests", "reviewed the file")
        - "tests passed" / "build succeeded" / "CI green" on their own, with no described change
        - session bookkeeping (session started, workspace loaded, context files read)
        - a task still in progress with no decision or conclusion yet
        - conversational context: what the user asked for in this session, user preferences not
          tied to the project, temporary instructions or plans, what the agent intended to do next
        - workflow/task/queue/draft/synthesis/agent IDs and other session-run bookkeeping, unless
          the identifier is itself durable project knowledge (e.g. a config key, not a run id)
        - unverified speculation or a working hypothesis stated as fact ("probably", "likely",
          confidence percentages) — only record a claim once it is verified against the repository;
          if something is genuinely important but unresolved, record it as the concrete open gap
          itself (e.g. "no reinstatement transition handler exists"), not as a guess about why
        - anything outside this repository: unrelated projects, the user's environment, personal
          context, or generic programming discussion with no repository-specific content

        Only propose a memory that is durable (useful weeks/months from now), project-specific
        (about this repository's code, config, infra, tests, or architecture), actionable (helps
        an engineer understand/debug/modify/test/navigate), and verified from the session material
        below rather than assumed.

        Prefer fewer, higher-signal memories over many small ones. Do not split one dominant
        thread into several candidates that each restate the same fact from a different angle —
        if a single thread clearly dominates the session and everything else is trivial, propose
        one memory for it, not a handful of near-duplicates. Only propose more than one memory
        when they describe genuinely distinct decisions, discoveries, or fixes. Do not invent
        diversity where none exists. Do not propose a memory for a class/function/file merely
        existing or being located somewhere — that's what a code index is for; only propose one
        when there is a non-obvious behavior, invariant, or relationship attached to it.

        KNOWLEDGE vs EVENT: every TYPE except Event is knowledge and must describe how the code
        behaves NOW, in the present tense, as an engineer reading it next month would need it.
        "Added X", "Upgraded Y to Z", "Fixed W" are change logs: either restate them as the current
        behavior they produced ("Authentication uses the OAuth2 PKCE flow") or, if the change itself
        is all there is to say, propose it as TYPE Event. Never put the prior behavior in FACTS
        ("previously ...", "used to ...") - that history is recorded separately.

        Titles must be outcome-focused (what is true or was learned), never process-focused (what
        you did to find out), and must be as concrete as the session material allows - name the
        actual thing, not a category of thing. For example:
        GOOD: "Authentication uses the OAuth2 PKCE flow"
        GOOD: "Flush strategy fails under concurrent sessions"
        BAD:  "Analyzed authentication implementation" (describes the analysis, not what was found)
        BAD:  "Reviewed session handling code" (process narration, no outcome)
        BAD:  "Embedding service architecture spans multiple modules" (true but says nothing an
              engineer could act on - which modules, and via what mechanism?)
        GOOD: "OllamaEmbeddingGeneratorDecorator caps concurrent embedding calls via SemaphoreSlim"
              (same underlying fact, but names the actual class and mechanism)

        BODY is the main payload and the most common failure mode is writing it too generically -
        a memory that only restates its own title in sentence form, or names a class/module without
        saying what it actually does or why it matters, is nearly useless when it resurfaces in a
        future session. Write BODY as if explaining to an engineer who was NOT in this session and
        needs to act on it immediately:
        - Name the SPECIFIC files (by path), classes/functions/symbols (by name), and config keys
          or settings actually involved, exactly as they appeared in the session material below -
          never fall back to a generic stand-in ("a service", "some files", "the config", "the
          module") when the concrete name is available.
        - State the actual mechanism or reasoning, not just that something happened: what the
          concrete problem or prior behavior was, what specifically changed or was found, and why
          that resolves/explains it (the causal link a reader would otherwise have to re-derive).
        - Prefer one dense, specific paragraph over several vague ones. A BODY that could be copy-
          pasted onto an unrelated memory of the same TYPE without anyone noticing is too generic -
          rewrite it with the details that make it true of THIS session and no other.
        Example, same underlying session fact as the title example above:
        BAD BODY:  "Embedding service architecture spans multiple modules via
                    ModuleEfConfigurationRegistry." (names one symbol but explains no mechanism,
                    no problem, no reasoning - just a label dressed as a sentence)
        GOOD BODY: "OllamaEmbeddingGeneratorDecorator (src/Context.Providers/Embeddings/
                    OllamaEmbeddingGeneratorDecorator.cs) wraps the raw Ollama client and limits
                    concurrent GenerateAsync calls to 4 via a SemaphoreSlim, because the shared
                    Ollama instance returned truncated/zero vectors when more than ~6 embedding
                    requests were in flight at once. EmbeddingLookupAdapter now awaits this
                    decorator instead of calling the raw generator directly."

        Respond in EXACTLY this format:

        MEMORY_COUNT: <n>
        MEMORY 1
        TYPE: <one of Fact, Decision, Constraint, Architecture, Behavior, Configuration, Discovery, Procedure, Issue, Event>
        TITLE: <short one-line title, under 100 characters>
        BODY: <2-5 sentences, self-contained, naming specific files/symbols/config keys and the actual mechanism or reasoning per the BODY guidance above - not a restatement of TITLE>
        TOPIC: <short 2-5 word phrase naming the feature/subsystem/component this memory is about>
        FACTS:
        - <fact 1>
        - <fact 2>
        CONCEPTS:
        - <concept 1>
        FILES_READ:
        - <path 1>
        FILES_MODIFIED:
        - <path 1>
        ENTITIES: <comma-separated concrete names this memory is about>
        END_MEMORY
        MEMORY 2
        ...
        END_MEMORY

        TOPIC is a short (2-5 word) phrase naming the feature, subsystem, or component this memory
        is about — e.g. "Auth middleware", "Embedding pipeline", "Job dispatcher retry logic". It
        is NOT a full sentence and NOT the memory's own TITLE restated: TITLE describes what this
        specific memory says, TOPIC names the broader thing it's about, so that memories from
        different sessions about the same area end up with the same TOPIC and can be found
        together later. Reuse an existing, natural name a reader/engineer would already use for
        that area of the codebase rather than inventing a new, overly specific one-off phrase for
        this session alone — prefer the more general, more likely-to-recur name when in doubt.

        FACTS is 2-5 short, atomic, standalone facts pulled out of this memory — each one must
        make sense read on its own, with no surrounding narrative, since it is shown when a
        search result needs to say something useful in very few tokens. Reuse the concrete names
        from BODY (a file path, a symbol, a number, a config key) rather than a vaguer paraphrase —
        a fact is a compressed restatement of something specific already said in BODY, not a new,
        softer summary of it.

        CONCEPTS is 1-3 tags naming what KIND of knowledge this memory is, chosen ONLY from this
        fixed list (do not invent new ones): how-it-works, why-it-exists, what-changed,
        problem-solution, gotcha, pattern, trade-off. This lets a future search filter by kind of
        insight instead of only matching text. Omit the CONCEPTS block entirely if none clearly apply.

        FILES_READ / FILES_MODIFIED are the specific files THIS memory is about — not every file
        touched in the whole session, only the ones this particular decision/fix/discovery
        concerns. Choose FILES_READ only from the "Files read" list below, and FILES_MODIFIED only
        from the "Changed files" list below (when the material has no such lists, use only paths
        that appear verbatim in it) — never invent a path, and never move a path from one
        list into the other (a file that was only read is not "modified" just because this memory
        discusses it). FILES_READ is for files that were examined/relevant but not changed;
        FILES_MODIFIED is for files that were actually edited because of this memory's content. Omit
        either block entirely if it doesn't apply (most memories only need FILES_MODIFIED, or neither).

        ENTITIES lists the concrete names this memory is about, exactly as written in the material:
        ticket keys (PAY-123), class/method/function names, config keys and environment variables,
        file names, packages and tools. Only names that identify THIS memory's subject - not the
        repository, language or framework every memory shares. Omit the line if there are none.

        If n is 0, respond with only "MEMORY_COUNT: 0" and nothing else.
        """;

    public static string BuildUserPromptFromEvents(Guid sessionId, IReadOnlyList<SessionEvent> events)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Session: {sessionId}");
        sb.AppendLine($"Session events, oldest first ({events.Count} total):");
        sb.AppendLine();

        var eventsStart = sb.Length;
        for (var i = 0; i < events.Count; i++)
        {
            if (sb.Length - eventsStart > MaxEventsSectionChars)
            {
                sb.AppendLine($"(... {events.Count - i} later events omitted for length)");
                break;
            }

            AppendEvent(sb, events[i]);
        }

        AppendList(sb, "Files read", PayloadFieldExtractor.ExtractDistinctPaths(events, SessionEventType.FileRead));
        AppendList(sb, "Changed files", PayloadFieldExtractor.ExtractDistinctPaths(events, SessionEventType.FileEdit));
        return sb.ToString();
    }

    public static string BuildUserPromptFromCompactSummary(Guid sessionId, string compactSummary)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Session: {sessionId}");
        sb.AppendLine("Conversation summary written when the session was compacted:");
        sb.AppendLine();
        sb.AppendLine(Truncate(compactSummary.Trim(), MaxCompactSummaryChars));
        return sb.ToString();
    }

    private static void AppendEvent(StringBuilder sb, SessionEvent evt)
    {
        sb.Append('[').Append(evt.Timestamp.UtcDateTime.ToString("HH:mm:ss")).Append("] ").Append(evt.Type);

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(evt.PayloadJson);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            sb.AppendLine().Append("  ").AppendLine(Truncate(evt.PayloadJson, MaxOtherPayloadChars));
            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine().Append("  ").AppendLine(Truncate(root.GetRawText(), MaxOtherPayloadChars));
            return;
        }

        if (GetString(root, "toolName") is { } toolName)
        {
            sb.Append(' ').Append(toolName);
        }

        if (GetString(root, "path") is { } path)
        {
            sb.Append(' ').Append(path);
        }

        sb.AppendLine();
        var known = new HashSet<string>(StringComparer.Ordinal) { "toolName", "path" };
        AppendField(sb, root, known, "prompt", null, MaxPromptChars);
        AppendField(sb, root, known, "message", null, MaxAssistantMessageChars);
        AppendField(sb, root, known, "toolInput", "input", MaxToolInputChars);
        AppendField(sb, root, known, "toolResponse", "output", MaxToolResponseChars);

        foreach (var property in root.EnumerateObject().Where(p => !known.Contains(p.Name)))
        {
            AppendValue(sb, property.Name, property.Value, MaxOtherPayloadChars);
        }
    }

    private static void AppendField(StringBuilder sb, JsonElement root, HashSet<string> known, string name, string? label, int maxChars)
    {
        known.Add(name);
        if (root.TryGetProperty(name, out var value))
        {
            AppendValue(sb, label, value, maxChars);
        }
    }

    private static void AppendValue(StringBuilder sb, string? label, JsonElement value, int maxChars)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
        if (string.IsNullOrWhiteSpace(text) || value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        sb.Append("  ");
        if (label is not null)
        {
            sb.Append(label).Append(": ");
        }

        sb.AppendLine(Truncate(text.Trim(), maxChars));
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : string.Concat(text.AsSpan(0, maxChars), "…");

    private static void AppendList(StringBuilder sb, string label, IReadOnlyList<string> items)
    {
        sb.AppendLine();
        sb.AppendLine($"{label}:");
        if (items.Count == 0)
        {
            sb.AppendLine("(none)");
            return;
        }

        foreach (var item in items)
        {
            sb.Append("- ").AppendLine(item);
        }
    }
}
