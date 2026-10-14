using System.Text;

namespace Context.Memory.Consolidation;

/// <summary>
/// Checks that a merged memory still carries everything its sources said: every fact (same near-duplicate
/// rule the fact merge uses) and every concrete name (file, symbol, config key, ticket). A model that
/// summarized away a detail is caught here instead of the detail silently disappearing.
/// </summary>
internal static class MergeVerifier
{
    /// <summary>Source facts that no merged fact covers, in source order.</summary>
    public static IReadOnlyList<string> MissingFacts(IEnumerable<string> sourceFacts, IReadOnlyList<string> mergedFacts) =>
        [.. sourceFacts
            .Select(f => f.Trim())
            .Where(f => f.Length > 0 && !mergedFacts.Any(kept => Covers(kept, f)))
            .Distinct(StringComparer.Ordinal)];

    /// <summary>Display forms of the strong names in the sources that the merged text never mentions. Sources are
    /// scanned one text at a time (the extractor caps one call's entities) and presence is a case-insensitive
    /// substring test, so a merged text naming the full path still counts for its basename.</summary>
    public static IReadOnlyList<string> MissingNames(IEnumerable<string> sourceTexts, IEnumerable<string> mergedTexts)
    {
        var merged = string.Join("\n", mergedTexts);
        var missing = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in sourceTexts)
        {
            foreach (var entity in EntityExtractor.ExtractFromText(text))
            {
                if (EntityExtractor.IsStrong(entity.Key)
                    && seen.Add(entity.Key)
                    && !merged.Contains(entity.Display, StringComparison.OrdinalIgnoreCase))
                {
                    missing.Add(entity.Display);
                }
            }
        }

        return missing;
    }

    /// <summary>The body with a closing line naming the missing identifiers, or null when that would pass <paramref name="maxLength"/>.</summary>
    public static string? AppendNames(string body, IReadOnlyList<string> names, int maxLength)
    {
        if (names.Count == 0)
        {
            return body;
        }

        var extended = new StringBuilder(body.TrimEnd()).Append("\n\nAlso referenced: ").Append(string.Join(", ", names)).Append('.').ToString();
        return extended.Length <= maxLength ? extended : null;
    }

    /// <summary>Near-duplicate wording, or the source fact contained word for word in a longer merged fact.</summary>
    public static bool Covers(string mergedFact, string sourceFact) =>
        FactMerger.AreNearDuplicates(mergedFact, sourceFact)
        || $" {FactMerger.NormalizeFact(mergedFact)} ".Contains($" {FactMerger.NormalizeFact(sourceFact)} ", StringComparison.Ordinal);
}
