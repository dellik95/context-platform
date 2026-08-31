namespace Context.Memory.Extraction;

/// <summary>
/// Judges whether a new candidate memory statement is consistent with (should reinforce)
/// or contradicts (should supersede) an existing, highly-similar memory.
/// </summary>
internal static class MemoryConsistencyChecker
{
    public const string SystemPrompt =
        """
        You judge whether a NEW statement about a software project is consistent with an OLD
        statement recorded earlier, or contradicts/supersedes it. Two statements about the same
        general topic that simply add detail are CONSISTENT. A statement that reverses,
        replaces, or invalidates the old one is CONTRADICTORY.

        Respond with exactly one word: CONSISTENT or CONTRADICTORY.
        """;

    public static string BuildUserPrompt(Context.Domain.Entities.Memory oldMemory, MemoryCandidate candidate)
    {
        return
            $"""
            OLD ({oldMemory.Type}): {oldMemory.Title}
            {oldMemory.Body}

            NEW ({candidate.Type}): {candidate.Title}
            {candidate.Body}
            """;
    }

    public static bool ParseIsConsistent(string llmResponse)
    {
        // Fail open toward "consistent" (reinforce) unless the response clearly says otherwise —
        // an unparsed/garbled response should not spuriously fork memory history.
        return !llmResponse.Contains("CONTRADICT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Offline heuristic used when no real LLM is configured: looks for simple
    /// reversal/replacement language in the new statement that the old statement doesn't
    /// already share.
    /// </summary>
    private static readonly string[] ContradictionMarkers =
    [
        "no longer", "instead of", "replaced", "replaces", "deprecated", "reverted",
        "removed", "abandoned", "changed from", "rather than", "not "
    ];

    public static bool HeuristicIsConsistent(Context.Domain.Entities.Memory oldMemory, MemoryCandidate candidate)
    {
        var oldText = $"{oldMemory.Title} {oldMemory.Body}".ToLowerInvariant();
        var newText = $"{candidate.Title} {candidate.Body}".ToLowerInvariant();

        return !ContradictionMarkers.Any(marker => newText.Contains(marker) && !oldText.Contains(marker));
    }
}
