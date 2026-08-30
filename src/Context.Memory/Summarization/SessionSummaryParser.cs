using System.Text;
using System.Text.RegularExpressions;

namespace Context.Memory.Summarization;

/// <summary>
/// Parses the strict-format structured response requested from the LLM in
/// <see cref="SessionSummaryPromptBuilder"/> back into its six sections.
/// </summary>
internal static partial class SessionSummaryParser
{
    private static readonly string[] SectionKeys =
    [
        "TITLE", "COMPLETED", "DISCOVERED", "DECISIONS", "CHANGED_FILES", "OPEN_ISSUES", "NEXT_STEPS"
    ];

    public sealed record ParsedSections(
        string? Title,
        string? Completed,
        string? Discovered,
        string? Decisions,
        IReadOnlyList<string> ChangedFiles,
        string? OpenIssues,
        string? NextSteps)
    {
        public bool IsEffectivelyEmpty =>
            string.IsNullOrWhiteSpace(Completed) &&
            string.IsNullOrWhiteSpace(Discovered) &&
            string.IsNullOrWhiteSpace(Decisions) &&
            ChangedFiles.Count == 0 &&
            string.IsNullOrWhiteSpace(OpenIssues) &&
            string.IsNullOrWhiteSpace(NextSteps);
    }

    public static ParsedSections Parse(string llmResponse)
    {
        var buckets = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in SectionKeys)
        {
            buckets[key] = new StringBuilder();
        }

        string? currentKey = null;
        foreach (var rawLine in (llmResponse ?? string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            var headerMatch = HeaderRegex().Match(rawLine);
            if (headerMatch.Success && SectionKeys.Contains(headerMatch.Groups[1].Value.ToUpperInvariant()))
            {
                currentKey = headerMatch.Groups[1].Value.ToUpperInvariant();
                var trailing = headerMatch.Groups[2].Value.Trim();
                if (trailing.Length > 0)
                {
                    buckets[currentKey].AppendLine(trailing);
                }

                continue;
            }

            if (currentKey is not null)
            {
                buckets[currentKey].AppendLine(rawLine);
            }
        }

        var changedFilesRaw = buckets["CHANGED_FILES"].ToString();
        var changedFiles = changedFilesRaw
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripBullet)
            .Where(line => line.Length > 0 && !IsNonePlaceholder(line))
            .Distinct()
            .ToList();

        var title = buckets["TITLE"].ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripBullet)
            .FirstOrDefault(line => line.Length > 0 && !IsNonePlaceholder(line));

        return new ParsedSections(
            title,
            Clean(buckets["COMPLETED"]),
            Clean(buckets["DISCOVERED"]),
            Clean(buckets["DECISIONS"]),
            changedFiles,
            Clean(buckets["OPEN_ISSUES"]),
            Clean(buckets["NEXT_STEPS"]));
    }

    private static string? Clean(StringBuilder sb)
    {
        var text = sb.ToString().Trim();
        return text.Length == 0 || IsNonePlaceholder(text) ? null : text;
    }

    private static string StripBullet(string line)
    {
        var trimmed = line.Trim();
        return BulletRegex().Replace(trimmed, string.Empty).Trim();
    }

    private static bool IsNonePlaceholder(string text) =>
        text.Trim('(', ')', ' ', '.', '-').Equals("none", StringComparison.OrdinalIgnoreCase);

    // A larger/real session (more prompt tokens) reliably nudges this model into markdown headings
    // ("### TITLE:") even though the prompt asks for a plain "TITLE:" line — confirmed live: a
    // 3-event test session parsed fine, a 40-event real one didn't, because this pattern only
    // tolerated a bullet prefix (-/*), not a heading prefix (#/##/###). Both are now accepted.
    [GeneratedRegex(@"^\s*(?:[-*#]+\s*)?([A-Za-z_]+)\s*:\s*(.*)$")]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^[-*]\s+")]
    private static partial Regex BulletRegex();
}
