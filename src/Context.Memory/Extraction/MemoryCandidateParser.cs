using System.Text;
using System.Text.RegularExpressions;
using Context.Domain.Enums;

namespace Context.Memory.Extraction;

/// <summary>Parses the strict-format response requested by <see cref="MemoryExtractionPromptBuilder"/>.</summary>
internal static partial class MemoryCandidateParser
{
    /// <summary>Which bulleted-list section (if any) subsequent bullet lines belong to.</summary>
    private enum ListSection { None, Facts, Concepts, FilesRead, FilesModified, Entities }

    /// <param name="source">Stamped on every candidate: the material the response was extracted from.</param>
    public static MemoryCandidateParseResult Parse(string llmResponse, EvidenceSource source)
    {
        var results = new List<MemoryCandidate>();
        var discardedBlocks = 0;
        if (string.IsNullOrWhiteSpace(llmResponse))
        {
            return new MemoryCandidateParseResult(results, discardedBlocks);
        }

        MemoryType? type = null;
        string? title = null;
        string? topic = null;
        var body = new StringBuilder();
        var facts = new List<string>();
        var concepts = new List<string>();
        var filesRead = new List<string>();
        var filesModified = new List<string>();
        var entities = new List<string>();
        var inBody = false;
        var currentList = ListSection.None;

        void Flush()
        {
            var hadContent = type is not null || !string.IsNullOrWhiteSpace(title) || body.Length > 0 || facts.Count > 0;

            if (type is not null && !string.IsNullOrWhiteSpace(title))
            {
                var readList = filesRead.ToList();
                var modifiedList = filesModified.ToList();
                results.Add(new MemoryCandidate(
                    type.Value,
                    title.Trim(),
                    body.ToString().Trim(),
                    source,
                    facts.ToList(),
                    concepts.ToList(),
                    readList,
                    modifiedList,
                    string.IsNullOrWhiteSpace(topic) ? null : topic.Trim(),
                    ScopePathHeuristics.DeriveFromFiles(modifiedList, readList))
                {
                    Entities = entities.ToList()
                });
            }
            else if (hadContent)
            {
                // A block that named neither a type nor a title cannot become a memory; counted so
                // the caller can log format drift instead of silently losing the content.
                discardedBlocks++;
            }

            type = null;
            title = null;
            topic = null;
            body.Clear();
            facts.Clear();
            concepts.Clear();
            filesRead.Clear();
            filesModified.Clear();
            entities.Clear();
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
                // Only a SECOND TYPE line starts a new candidate; a TYPE that follows TITLE in the
                // same block would otherwise flush the title away and drop the whole candidate.
                if (type is not null)
                {
                    Flush();
                }

                type = ParseType(typeMatch.Groups[1].Value);
                inBody = false;
                currentList = ListSection.None;
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

            // Confidence is derived from evidence, not self-reported; the line is swallowed so it
            // cannot leak into a body.
            if (ConfidenceRegex().IsMatch(trimmed))
            {
                inBody = false;
                currentList = ListSection.None;
                continue;
            }

            var entitiesMatch = EntitiesRegex().Match(trimmed);
            if (entitiesMatch.Success)
            {
                entities.Clear();
                inBody = false;
                var inline = entitiesMatch.Groups[1].Value.Trim();
                if (inline.Length == 0)
                {
                    currentList = ListSection.Entities;
                }
                else
                {
                    entities.AddRange(inline.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    currentList = ListSection.None;
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
                            ListSection.Entities => entities,
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
        return new MemoryCandidateParseResult(results, discardedBlocks);
    }

    private static MemoryType ParseType(string raw)
    {
        MemoryTypeExtensions.TryParseName(raw, out var type);
        return type;
    }

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

    [GeneratedRegex(@"^CONFIDENCE:", RegexOptions.IgnoreCase)]
    private static partial Regex ConfidenceRegex();

    [GeneratedRegex(@"^ENTITIES:\s*(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex EntitiesRegex();

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
