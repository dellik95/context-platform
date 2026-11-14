using System.Data.Common;
using System.Text;

namespace Context.Application.Adapters;

/// <summary>
/// Pure tokenization, FTS5/LIKE query shaping, snippet extraction, and ranking helpers used by
/// <see cref="SqliteSearchIndex"/>.
/// </summary>
internal static class SqliteSearchQueryHelper
{
    public const int SnippetLength = 400;
    private const int MaxScopeTerms = 16;

    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "this", "that", "these", "those",
        "what", "when", "where", "which", "who", "whom", "whose", "why", "how",
        "are", "was", "were", "been", "being", "have", "has", "had", "does", "did",
        "can", "could", "would", "should", "will", "shall", "may", "might", "must",
        "into", "onto", "about", "after", "before", "between", "under", "over",
        "find", "show", "give", "list", "get", "all", "any", "not", "use", "using"
    };

    public static IReadOnlyList<string> NormalizeScope(IReadOnlyList<string>? scope) =>
        scope is null
            ? []
            : [.. scope.Where(s => !string.IsNullOrWhiteSpace(s))
                       .Select(s => s.Trim().ToLowerInvariant())
                       .Distinct()
                       .Take(MaxScopeTerms)];

    public static string BuildScopeSql(IReadOnlyList<string> scopeTerms)
    {
        if (scopeTerms.Count == 0)
        {
            return string.Empty;
        }

        var clauses = scopeTerms.Select((_, i) =>
            $"""lower(f."RelativePath") LIKE @scopePath{i} ESCAPE '\' OR lower(f."Language") = @scopeTerm{i}""");

        return $" AND ({string.Join(" OR ", clauses)})";
    }

    public static void AddScopeParameters(DbCommand command, IReadOnlyList<string> scopeTerms)
    {
        for (var i = 0; i < scopeTerms.Count; i++)
        {
            AddParameter(command, $"@scopePath{i}", $"%{EscapeLike(scopeTerms[i])}%");
            AddParameter(command, $"@scopeTerm{i}", scopeTerms[i]);
        }
    }

    public static bool MatchesScope(string relativePath, string? language, IReadOnlyList<string> scopeTerms) =>
        scopeTerms.Any(term =>
            relativePath.Contains(term, StringComparison.OrdinalIgnoreCase)
            || string.Equals(language, term, StringComparison.OrdinalIgnoreCase));

    public static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public static IReadOnlyList<string> ExtractSearchTerms(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var rawTokens = text
            .Split([' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '-', '_'],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length > 0);

        var expanded = new List<string>();
        foreach (var raw in rawTokens)
        {
            expanded.Add(raw);
            SplitCamelCase(raw, expanded);
        }

        var filtered = expanded
            .Where(t => !Stopwords.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToList();

        if (filtered.Count == 0)
        {
            filtered = expanded
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToList();
        }

        return filtered;
    }

    public static string? BuildMatchExpressionFromTerms(IReadOnlyList<string> terms)
    {
        var trigramTerms = terms.Where(t => t.Length >= 3).Take(24).ToList();
        if (trigramTerms.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < trigramTerms.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(" OR ");
            }

            sb.Append('"').Append(trigramTerms[i].Replace("\"", "\"\"")).Append('"');
        }

        return sb.ToString();
    }

    public static string ExtractSnippet(string text, IReadOnlyList<string> terms)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= SnippetLength)
        {
            return text;
        }

        var earliestMatch = -1;
        foreach (var term in terms)
        {
            var idx = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0 && (earliestMatch < 0 || idx < earliestMatch))
            {
                earliestMatch = idx;
            }
        }

        if (earliestMatch < 0 || earliestMatch < SnippetLength - 100)
        {
            return text[..SnippetLength];
        }

        var start = Math.Max(0, earliestMatch - 100);
        // Align start cleanly to a nearby line boundary so snippet stays legible.
        var newline = text.IndexOf('\n', start, Math.Min(40, text.Length - start));
        if (newline >= start && newline < earliestMatch)
        {
            start = newline + 1;
        }

        var length = Math.Min(SnippetLength, text.Length - start);
        var slice = text.Substring(start, length);
        return start > 0 ? $"…{slice}" : slice;
    }

    public static double AdjustLexicalScore(
        double baseScore,
        IReadOnlyList<string> terms,
        string? label,
        string? relativePath,
        bool preferNonTest)
    {
        var score = Math.Max(0.01, baseScore);

        if (!string.IsNullOrWhiteSpace(label) && terms.Count > 0)
        {
            var dot = label.LastIndexOf('.');
            var shortName = dot >= 0 && dot + 1 < label.Length ? label[(dot + 1)..] : label;

            var hitsInLabel = 0;
            var exactSymbolMatch = false;
            foreach (var term in terms)
            {
                if (string.Equals(shortName, term, StringComparison.OrdinalIgnoreCase))
                {
                    exactSymbolMatch = true;
                    hitsInLabel++;
                }
                else if (label.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    hitsInLabel++;
                }
            }

            if (hitsInLabel > 0)
            {
                var ratio = (double)hitsInLabel / terms.Count;
                score = score * (1.0 + (0.45 * ratio)) + (exactSymbolMatch ? 1.0 : 0.25 * ratio);
            }
        }

        if (preferNonTest && IsTestPath(relativePath))
        {
            score *= 0.75;
        }

        return score;
    }

    public static bool ShouldDownrankTests(IReadOnlyList<string> terms, IReadOnlyList<string> scopeTerms)
    {
        if (scopeTerms.Any(IsTestPath))
        {
            return false;
        }

        foreach (var term in terms)
        {
            if (term.StartsWith("test", StringComparison.OrdinalIgnoreCase)
                || term.StartsWith("spec", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsTestPath(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return false;
        }

        return relativePath.Contains("/test", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("\\test", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
            || relativePath.StartsWith("test/", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("Tests.", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains(".test.", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains(".spec.", StringComparison.OrdinalIgnoreCase);
    }

    public static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void SplitCamelCase(string token, List<string> target)
    {
        if (token.Length < 4)
        {
            return;
        }

        var start = 0;
        for (var i = 1; i < token.Length; i++)
        {
            var prev = token[i - 1];
            var curr = token[i];
            var boundary = (char.IsLower(prev) && char.IsUpper(curr))
                || (char.IsLetter(prev) && char.IsDigit(curr))
                || (char.IsDigit(prev) && char.IsLetter(curr))
                || (i + 1 < token.Length && char.IsUpper(prev) && char.IsUpper(curr) && char.IsLower(token[i + 1]));
            if (!boundary)
            {
                continue;
            }

            if (i - start >= 2)
            {
                target.Add(token[start..i]);
            }

            start = i;
        }

        if (start > 0 && token.Length - start >= 2)
        {
            target.Add(token[start..]);
        }
    }
}
