namespace Context.Retrieval;

/// <summary>
/// Keyword-match query intent, not an NLP classifier: routes "what's still pending?" style queries
/// differently from a neutral one.
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
