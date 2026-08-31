using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Memory.Extraction;

/// <summary>
/// Offline candidate generation used when no real LLM is configured: turns each non-empty
/// section of a <see cref="SessionSummary"/> into at most one candidate memory. Lower
/// confidence than an LLM-derived candidate since no judgment was applied.
/// </summary>
internal static class MemoryCandidateHeuristics
{
    private const int MaxTitleLength = 100;
    private const int MaxFacts = 3;

    public static IReadOnlyList<MemoryCandidate> FromSummary(SessionSummary summary)
    {
        var candidates = new List<MemoryCandidate>();
        var changedFiles = ParseChangedFiles(summary.ChangedFilesJson);

        // No LLM judgment is available offline to say WHICH files a Decision/Discovery/Issue
        // candidate is specifically about, so those three don't get FilesModified populated here
        // (an empty list is honest; guessing "all session files" for each would be misleading).
        TryAdd(candidates, MemoryType.Decision, summary.Decisions, 0.6);
        TryAdd(candidates, MemoryType.Discovery, summary.Discovered, 0.55);
        TryAdd(candidates, MemoryType.Issue, summary.OpenIssues, 0.5);
        TryAddChangedFiles(candidates, changedFiles);

        return candidates;
    }

    private static void TryAdd(List<MemoryCandidate> candidates, MemoryType type, string? section, double confidence)
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
        candidates.Add(new MemoryCandidate(type, title, section.Trim(), confidence, ExtractFacts(section), [], [], []));
    }

    private static void TryAddChangedFiles(List<MemoryCandidate> candidates, IReadOnlyList<string> files)
    {
        if (files.Count == 0)
        {
            return;
        }

        var title = Truncate($"Changed {files.Count} file(s): {string.Join(", ", files.Take(3))}", MaxTitleLength);
        var body = "Files touched this session:\n" + string.Join('\n', files.Select(f => $"- {f}"));
        var facts = files.Take(MaxFacts).Select(f => $"Changed {f}").ToList();
        // This candidate IS specifically about these files (that's its whole content), unlike the
        // Decision/Discovery/Issue candidates above — FilesModified is accurate here, not a guess.
        candidates.Add(new MemoryCandidate(MemoryType.Change, title, body, 0.5, facts, [], [], files));
    }

    private static IReadOnlyList<string> ParseChangedFiles(string? changedFilesJson)
    {
        if (string.IsNullOrWhiteSpace(changedFilesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(changedFilesJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
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
