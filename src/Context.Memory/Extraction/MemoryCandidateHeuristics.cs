using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Memory.Extraction;

/// <summary>
/// Offline candidate generation used when no real LLM is configured: turns each non-empty
/// section of a <see cref="SessionSummaryChunk"/> into at most one candidate memory, sourced as
/// <see cref="EvidenceSource.Heuristic"/> since no judgment was applied.
/// </summary>
internal static class MemoryCandidateHeuristics
{
    private const int MaxTitleLength = 100;
    private const int MaxFacts = 3;

    public static IReadOnlyList<MemoryCandidate> FromSummary(SessionSummaryChunk chunk)
    {
        var candidates = new List<MemoryCandidate>();

        // No LLM judgment is available offline to say WHICH files a Decision/Discovery/Issue
        // candidate is specifically about, so those three don't get FilesModified populated here
        // (an empty list is honest; guessing "all session files" for each would be misleading).
        TryAdd(candidates, MemoryType.Decision, chunk.Decisions);
        TryAdd(candidates, MemoryType.Discovery, chunk.Discovered);
        TryAdd(candidates, MemoryType.Issue, chunk.OpenIssues);

        return candidates;
    }

    private static void TryAdd(List<MemoryCandidate> candidates, MemoryType type, string? section)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            return;
        }

        var firstLine = section
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripBullet)
            .FirstOrDefault(l => l.Length > 0);

        if (firstLine is null)
        {
            return;
        }

        var title = Truncate(firstLine, MaxTitleLength);
        candidates.Add(new MemoryCandidate(type, title, section.Trim(), EvidenceSource.Heuristic, ExtractFacts(section), [], [], []));
    }

    /// <summary>Turns a section's first few non-empty lines into standalone facts — a cheap
    /// stand-in for the LLM-authored facts, since there is no model judgment available offline.</summary>
    private static IReadOnlyList<string> ExtractFacts(string section) =>
        section
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripBullet)
            .Where(l => l.Length > 0)
            .Take(MaxFacts)
            .Select(l => Truncate(l, MaxTitleLength))
            .ToList();

    private static string StripBullet(string line) => line.TrimStart('-', '*', ' ');

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}
