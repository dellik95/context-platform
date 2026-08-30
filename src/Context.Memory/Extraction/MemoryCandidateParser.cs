using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Context.Domain.Enums;

namespace Context.Memory.Extraction;

/// <summary>Parses the strict-format response requested by <see cref="MemoryExtractionPromptBuilder"/>.</summary>
internal static partial class MemoryCandidateParser
{
    private const double DefaultConfidence = 0.7;

    public static IReadOnlyList<MemoryCandidate> Parse(string llmResponse)
    {
        var results = new List<MemoryCandidate>();
        if (string.IsNullOrWhiteSpace(llmResponse))
        {
            return results;
        }

        MemoryType? type = null;
        string? title = null;
        var confidence = DefaultConfidence;
        var body = new StringBuilder();
        var facts = new List<string>();
        var inBody = false;
        var inFacts = false;

        void Flush()
        {
            if (type is not null && !string.IsNullOrWhiteSpace(title))
            {
                results.Add(new MemoryCandidate(type.Value, title.Trim(), body.ToString().Trim(), confidence, facts.ToList()));
            }

            type = null;
            title = null;
            confidence = DefaultConfidence;
            body.Clear();
            facts.Clear();
            inBody = false;
            inFacts = false;
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
                continue;
            }

            var confidenceMatch = ConfidenceRegex().Match(trimmed);
            if (confidenceMatch.Success)
            {
                inBody = false;
                inFacts = false;
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
                inFacts = false;
                continue;
            }

            if (FactsHeaderRegex().IsMatch(trimmed))
            {
                facts.Clear();
                inBody = false;
                inFacts = true;
                continue;
            }

            if (inFacts)
            {
                // Malformed/missing bullets for this one field shouldn't fail the whole
                // candidate — just skip lines that aren't recognizable fact bullets.
                var factMatch = FactBulletRegex().Match(trimmed);
                if (factMatch.Success)
                {
                    var fact = factMatch.Groups[1].Value.Trim();
                    if (fact.Length > 0)
                    {
                        facts.Add(fact);
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

    [GeneratedRegex(@"^CONFIDENCE:\s*([0-9.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ConfidenceRegex();

    [GeneratedRegex(@"^FACTS:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex FactsHeaderRegex();

    [GeneratedRegex(@"^[-*]\s*(.+)$")]
    private static partial Regex FactBulletRegex();
}
