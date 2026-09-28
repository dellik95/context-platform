namespace Context.Memory.Consolidation;

/// <summary>Unions fact lists without losing any fact and without keeping near-duplicate wordings.</summary>
public static class FactMerger
{
    /// <summary>Token-set Jaccard at or above which two facts are the same fact reworded.</summary>
    public const double NearDuplicateJaccard = 0.8;

    /// <summary><paramref name="existing"/> in order, followed by each incoming fact that is not a
    /// near-duplicate of one already kept.</summary>
    public static IReadOnlyList<string> Merge(IReadOnlyList<string> existing, IReadOnlyList<string> incoming)
    {
        var merged = new List<string>(existing.Count + incoming.Count);
        foreach (var fact in existing.Concat(incoming))
        {
            var trimmed = fact.Trim();
            if (trimmed.Length > 0 && !merged.Any(kept => AreNearDuplicates(kept, trimmed)))
            {
                merged.Add(trimmed);
            }
        }

        return merged;
    }

    public static bool AreNearDuplicates(string a, string b)
    {
        var left = NormalizeFact(a);
        var right = NormalizeFact(b);
        if (left == right)
        {
            return true;
        }

        return TextTokens.Jaccard(
            new HashSet<string>(left.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal),
            new HashSet<string>(right.Split(' ', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal)) >= NearDuplicateJaccard;
    }

    /// <summary>Lowercase alphanumeric tokens separated by single spaces.</summary>
    public static string NormalizeFact(string fact) => TextTokens.Normalize(fact);
}
