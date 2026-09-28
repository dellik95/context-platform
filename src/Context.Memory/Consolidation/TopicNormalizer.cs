namespace Context.Memory.Consolidation;

/// <summary>
/// Reduces a free-text topic to a grouping key so "Checkout page flow" and "checkout pages" land
/// together: lowercase words, punctuation stripped, stopwords and filler dropped, simple plurals
/// singularized, joined with '-'.
/// </summary>
public static class TopicNormalizer
{
    public const int MaxKeyLength = 100;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "an", "the", "of", "for", "in", "on", "and", "or", "to", "with", "via", "by", "at", "from", "is", "are",
    };

    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "page", "screen", "flow", "logic", "handling", "behavior", "behaviour", "detail", "overview", "misc",
        "general", "related", "stuff", "thing",
    };

    /// <summary>The topic key, or null for a blank topic.</summary>
    public static string? Normalize(string? topic)
    {
        var words = TextTokens.Tokenize(topic)
            .Select(w => w.Replace("'", "", StringComparison.Ordinal))
            .Where(w => w.Length > 0)
            .Select(Singularize)
            .ToList();
        if (words.Count == 0)
        {
            return null;
        }

        var kept = words.Where(w => !Stopwords.Contains(w) && !Filler.Contains(w)).ToList();
        var key = string.Join('-', (kept.Count > 0 ? kept : words).Distinct(StringComparer.Ordinal));
        return key.Length <= MaxKeyLength ? key : key[..MaxKeyLength].TrimEnd('-');
    }

    private static string Singularize(string word)
    {
        if (word.Length <= 3)
        {
            return word;
        }

        if (word.EndsWith("ies", StringComparison.Ordinal))
        {
            return word[..^3] + "y";
        }

        if (word.EndsWith("sses", StringComparison.Ordinal))
        {
            return word[..^2];
        }

        return word.EndsWith('s')
            && !word.EndsWith("ss", StringComparison.Ordinal)
            && !word.EndsWith("us", StringComparison.Ordinal)
            && !word.EndsWith("is", StringComparison.Ordinal)
                ? word[..^1]
                : word;
    }
}
