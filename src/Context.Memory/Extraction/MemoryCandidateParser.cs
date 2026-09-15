using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Context.Domain.Enums;

namespace Context.Memory.Extraction;

/// <summary>Parses the strict-format response requested by <see cref="MemoryExtractionPromptBuilder"/>.</summary>
internal static partial class MemoryCandidateParser
{
    private const double DefaultConfidence = 0.7;

    /// <summary>Which bulleted-list section (if any) subsequent bullet lines belong to.</summary>
    private enum ListSection { None, Facts, Concepts, FilesRead, FilesModified }

    public static IReadOnlyList<MemoryCandidate> Parse(string llmResponse)
    {
        var results = new List<MemoryCandidate>();
        if (string.IsNullOrWhiteSpace(llmResponse))
        {
            return results;
        }

        MemoryType? type = null;
        string? title = null;
        string? topic = null;
        var confidence = DefaultConfidence;
        var body = new StringBuilder();
        var facts = new List<string>();
        var concepts = new List<string>();
        var filesRead = new List<string>();
        var filesModified = new List<string>();
        var inBody = false;
        var currentList = ListSection.None;

        void Flush()
        {
            if (type is not null && !string.IsNullOrWhiteSpace(title))
            {
                var readList = filesRead.ToList();
                var modifiedList = filesModified.ToList();
                results.Add(new MemoryCandidate(
                    type.Value,
                    title.Trim(),
                    body.ToString().Trim(),
                    confidence,
                    facts.ToList(),
                    concepts.ToList(),
                    readList,
                    modifiedList,
                    string.IsNullOrWhiteSpace(topic) ? null : topic.Trim(),
                    ScopePathHeuristics.DeriveFromFiles(modifiedList, readList)));
            }

            type = null;
            title = null;
            topic = null;
            confidence = DefaultConfidence;
            body.Clear();
            facts.Clear();
            concepts.Clear();
            filesRead.Clear();
            filesModified.Clear();
            inBody = false;
            currentList = ListSection.None;
        }

        foreach (var rawLine in llmResponse.Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = rawLine.Trim();

            if (trimmed.Equals("END_MEMORY", StringComparison.OrdinalIgnoreCase) ||
                MemoryHeaderRegex().IsMatch(trimmed))
            {
                Flush();
                continue;
            }

            var typeMatch = TypeRegex().Match(trimmed);
            if (typeMatch.Success)
            {
                Flush();
                type = ParseType(typeMatch.Groups[1].Value);
                continue;
            }

            var titleMatch = TitleRegex().Match(trimmed);
            if (titleMatch.Success)
            {
                title = titleMatch.Groups[1].Value.Trim();
                inBody = false;
                currentList = ListSection.None;
                continue;
            }

            var confidenceMatch = ConfidenceRegex().Match(trimmed);
            if (confidenceMatch.Success)
            {
                inBody = false;
                currentList = ListSection.None;
                if (double.TryParse(confidenceMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedConfidence))
                {
                    confidence = Math.Clamp(parsedConfidence, 0.0, 1.0);
                }

                continue;
            }

            var bodyMatch = BodyRegex().Match(trimmed);
            if (bodyMatch.Success)
            {
                body.Clear();
                body.AppendLine(bodyMatch.Groups[1].Value.Trim());
                inBody = true;
                currentList = ListSection.None;
                continue;
            }

            var topicMatch = TopicRegex().Match(trimmed);
            if (topicMatch.Success)
            {
                topic = topicMatch.Groups[1].Value.Trim();
                inBody = false;
                currentList = ListSection.None;
                continue;
            }

            if (FactsHeaderRegex().IsMatch(trimmed))
            {
                facts.Clear();
                inBody = false;
                currentList = ListSection.Facts;
                continue;
            }

            if (ConceptsHeaderRegex().IsMatch(trimmed))
            {
                concepts.Clear();
                inBody = false;
                currentList = ListSection.Concepts;
                continue;
            }

            if (FilesReadHeaderRegex().IsMatch(trimmed))
            {
                filesRead.Clear();
                inBody = false;
                currentList = ListSection.FilesRead;
                continue;
            }

            if (FilesModifiedHeaderRegex().IsMatch(trimmed))
            {
                filesModified.Clear();
                inBody = false;
                currentList = ListSection.FilesModified;
                continue;
            }

            if (currentList != ListSection.None)
            {
                // Malformed/missing bullets for this one field shouldn't fail the whole
                // candidate — just skip lines that aren't recognizable bullets.
                var bulletMatch = FactBulletRegex().Match(trimmed);
                if (bulletMatch.Success)
                {
                    var value = bulletMatch.Groups[1].Value.Trim();
                    if (value.Length > 0)
                    {
                        var target = currentList switch
                        {
                            ListSection.Facts => facts,
                            ListSection.Concepts => concepts,
                            ListSection.FilesRead => filesRead,
                            ListSection.FilesModified => filesModified,
                            _ => null,
                        };
                        target?.Add(value);
                    }
                }

                continue;
            }

            if (inBody && trimmed.Length > 0)
            {
                body.AppendLine(trimmed);
            }
        }

        Flush();
        return results;
    }

    private static MemoryType ParseType(string raw) =>
        Enum.TryParse<MemoryType>(raw.Trim(), ignoreCase: true, out var parsed) ? parsed : MemoryType.Discovery;

    [GeneratedRegex(@"^MEMORY\s*\d*\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex MemoryHeaderRegex();

    [GeneratedRegex(@"^TYPE:\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TypeRegex();

    [GeneratedRegex(@"^TITLE:\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"^BODY:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex BodyRegex();

    [GeneratedRegex(@"^TOPIC:\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex TopicRegex();

    [GeneratedRegex(@"^CONFIDENCE:\s*([0-9.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ConfidenceRegex();

    [GeneratedRegex(@"^FACTS:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FactsHeaderRegex();

    [GeneratedRegex(@"^CONCEPTS:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ConceptsHeaderRegex();

    [GeneratedRegex(@"^FILES_READ:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FilesReadHeaderRegex();

    [GeneratedRegex(@"^FILES_MODIFIED:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FilesModifiedHeaderRegex();

    [GeneratedRegex(@"^[-*]\s*(.+)$")]
    private static partial Regex FactBulletRegex();
}
