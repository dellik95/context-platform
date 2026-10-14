using System.Text.RegularExpressions;
using Context.Domain.Enums;

namespace Context.Memory.Extraction;

/// <summary>
/// Quality gate between producers and consolidation. A truncated or hallucinated response otherwise
/// reaches the database as an empty-bodied memory indistinguishable from a real one.
/// </summary>
internal static partial class MemoryCandidateValidator
{
    internal const int MaxTitleLength = 200;
    internal const int MaxBodyLength = 8_000;
    internal const int MaxFacts = 20;
    internal const int MaxFactLength = 500;
    internal const int MaxListEntries = 50;
    internal const int MaxTopicLength = 100;

    /// <summary>
    /// The candidate normalized to the persistence limits, or null with <paramref name="reason"/> set
    /// when it carries too little to be worth storing. A non-explicit pure change log is rejected:
    /// memory describes how things are now, not what a session did.
    /// </summary>
    public static MemoryCandidate? Normalize(MemoryCandidate candidate, out string? reason, int maxFacts = MaxFacts, int maxBodyLength = MaxBodyLength)
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

        if (candidate.Source != EvidenceSource.Explicit && IsChangeLog(title, candidate.Facts))
        {
            reason = "change log";
            return null;
        }

        reason = null;
        return candidate with
        {
            Title = Truncate(title, MaxTitleLength),
            Body = Truncate(body, maxBodyLength),
            Facts = [.. candidate.Facts
                .Select(f => f.Trim())
                .Where(f => f.Length > 0)
                .Select(f => Truncate(f, MaxFactLength))
                .Take(maxFacts)],
            Concepts = Limit(candidate.Concepts),
            FilesRead = Limit(candidate.FilesRead),
            FilesModified = Limit(candidate.FilesModified),
            Entities = Limit(candidate.Entities),
            Topic = string.IsNullOrWhiteSpace(candidate.Topic) ? null : Truncate(candidate.Topic.Trim(), MaxTopicLength)
        };
    }

    /// <summary>A change-log title ("Added X", "Upgraded Y") with no fact that states anything but another change.</summary>
    public static bool IsChangeLog(string title, IEnumerable<string> facts) =>
        IsChangeLogStatement(title) && facts.Where(f => !string.IsNullOrWhiteSpace(f)).All(IsChangeLogStatement);

    private static bool IsChangeLogStatement(string text) => ChangeLogRegex().IsMatch(text.Trim());

    private static IReadOnlyList<string> Limit(IReadOnlyList<string> values) =>
        [.. values.Select(v => v.Trim()).Where(v => v.Length > 0).Take(MaxListEntries)];

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    [GeneratedRegex(@"^(?:added|updated|changed|implemented|fixed|removed|refactored|renamed|upgraded|migrated|introduced|replaced|modified|deleted|bumped|moved|created|reverted|switched)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChangeLogRegex();
}
