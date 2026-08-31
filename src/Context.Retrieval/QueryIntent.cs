namespace Context.Retrieval;

/// <summary>
/// Basic query-intent detection for the codemem-inspired hybrid pack re-ranking refinements
/// (ROADMAP.md). Deliberately a small keyword-match helper, not an NLP classifier: good enough to
/// route "what's still pending?" and "what did we do last time?" style queries differently from a
/// neutral one, shared between <see cref="ContextPackBuilder"/> (reordering the notes/issues text
/// it already has) and <see cref="Context.Application.ContextEngine"/> (which has the underlying
/// <see cref="Context.Domain.Entities.Memory"/> timestamps <see cref="ContextPackBuilder"/> does
/// not).
/// </summary>
public static class QueryIntent
{
    /// <summary>
    /// Substrings identifying a "what's left to do" query. Tune here -- matched case-insensitively
    /// against the raw query text.
    /// </summary>
    public static readonly IReadOnlyList<string> PendingWorkKeywords =
    [
        "todo",
        "to-do",
        "to do",
        "pending",
        "next steps",
        "next step",
        "open issues",
        "open issue",
        "what's left",
        "whats left",
        "remaining",
        "still need",
        "still open",
        "unresolved",
        "outstanding",
    ];

    /// <summary>
    /// Substrings identifying a "what happened before" query. Tune here -- matched
    /// case-insensitively against the raw query text.
    /// </summary>
    public static readonly IReadOnlyList<string> RecallKeywords =
    [
        "what did we do",
        "what we did",
        "last time",
        "previously",
        "before",
    ];

    /// <summary>
    /// Substrings identifying pending-work/next-steps flavored note content (as opposed to the
    /// query text itself) -- used to prioritize which already-selected notes/issues surface first
    /// once <see cref="IsPendingWork"/> intent is detected.
    /// </summary>
    public static readonly IReadOnlyList<string> PendingWorkContentKeywords =
    [
        "todo",
        "to-do",
        "next step",
        "pending",
        "remaining",
        "unresolved",
        "blocked",
        "in progress",
        "not yet",
        "still need",
    ];

    public static bool IsPendingWork(string queryText) => ContainsAny(queryText, PendingWorkKeywords);

    public static bool IsRecall(string queryText) => ContainsAny(queryText, RecallKeywords);

    /// <summary>Whether a piece of note/issue text itself reads as pending-work/next-steps flavored.</summary>
    public static bool IsPendingWorkFlavored(string noteText) => ContainsAny(noteText, PendingWorkContentKeywords);

    private static bool ContainsAny(string text, IReadOnlyList<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
