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
    /// when it carries too little to be worth storing. A non-explicit knowledge candidate titled as a
    /// change log ("Added X", "Upgraded Y") is reclassified as an Event unless
    /// <paramref name="reclassifyChangeLog"/> is false: knowledge describes how things are now, not what
    /// a session did.
    /// </summary>
    public static MemoryCandidate? Normalize(
        MemoryCandidate candidate, out string? reason, bool reclassifyChangeLog = true, int maxFacts = MaxFacts)
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

        var type = reclassifyChangeLog
            && candidate.Type.IsKnowledge()
            && candidate.Source != EvidenceSource.Explicit
            && IsChangeLogTitle(title)
                ? MemoryType.Event
                : candidate.Type;

        reason = null;
        return candidate with
        {
            Type = type,
            Title = Truncate(title, MaxTitleLength),
            Body = Truncate(body, MaxBodyLength),
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

    public static bool IsChangeLogTitle(string title) => ChangeLogTitleRegex().IsMatch(title.Trim());

    private static IReadOnlyList<string> Limit(IReadOnlyList<string> values) =>
        [.. values.Select(v => v.Trim()).Where(v => v.Length > 0).Take(MaxListEntries)];

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    [GeneratedRegex(@"^(?:added|updated|changed|implemented|fixed|removed|refactored|renamed|upgraded|migrated|introduced|replaced|modified|deleted|bumped|moved|created|reverted|switched)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChangeLogTitleRegex();
}
