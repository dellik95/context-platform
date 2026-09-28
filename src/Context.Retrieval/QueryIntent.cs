namespace Context.Retrieval;

/// <summary>
/// Keyword-match query intent, not an NLP classifier: routes "what's still pending?" and
/// "what changed?" style queries differently from a neutral one.
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
    /// Substrings identifying a change-history query, the only kind that surfaces Event memories
    /// in a context pack.
    /// </summary>
    public static readonly IReadOnlyList<string> ChangeHistoryKeywords =
    [
        "what changed",
        "when did",
        "history",
        "timeline",
        "last time",
        "recently",
        "changelog",
        "what did we do",
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

    public static bool IsChangeHistory(string queryText) => ContainsAny(queryText, ChangeHistoryKeywords);

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
