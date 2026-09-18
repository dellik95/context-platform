namespace Context.Memory.Extraction;

/// <summary>
/// Quality gate between the parser and the store. A truncated or hallucinated LLM response
/// otherwise reaches the database as an empty-bodied memory at the default confidence, where it is
/// indistinguishable from a real one and can never be cleaned up automatically.
/// </summary>
internal static class MemoryCandidateValidator
{
    internal const int MaxTitleLength = 200;
    internal const int MaxBodyLength = 8_000;
    internal const int MaxFacts = 20;
    internal const int MaxFactLength = 500;
    internal const int MaxListEntries = 50;

    /// <summary>
    /// The candidate normalized to the persistence limits, or null with <paramref name="reason"/>
    /// set when it carries too little to be worth storing.
    /// </summary>
    public static MemoryCandidate? Normalize(MemoryCandidate candidate, out string? reason)
    {
        var title = candidate.Title.Trim();
        var body = candidate.Body.Trim();

        if (title.Length == 0)
        {
            reason = "empty title";
            return null;
        }

        if (body.Length == 0)
        {
            reason = "empty body";
            return null;
        }

        reason = null;
        return candidate with
        {
            Title = Truncate(title, MaxTitleLength),
            Body = Truncate(body, MaxBodyLength),
            Confidence = Math.Clamp(candidate.Confidence, 0.0, 1.0),
            Facts = [.. candidate.Facts
                .Select(f => f.Trim())
                .Where(f => f.Length > 0)
                .Select(f => Truncate(f, MaxFactLength))
                .Take(MaxFacts)],
            Concepts = Limit(candidate.Concepts),
            FilesRead = Limit(candidate.FilesRead),
            FilesModified = Limit(candidate.FilesModified),
            Topic = string.IsNullOrWhiteSpace(candidate.Topic) ? null : Truncate(candidate.Topic.Trim(), MaxTitleLength)
        };
    }

    private static IReadOnlyList<string> Limit(IReadOnlyList<string> values) =>
        [.. values.Select(v => v.Trim()).Where(v => v.Length > 0).Take(MaxListEntries)];

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
