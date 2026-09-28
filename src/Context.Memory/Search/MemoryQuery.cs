using Context.Memory.Consolidation;

namespace Context.Memory.Search;

/// <summary>
/// A memory query parsed once: the trimmed text for whole-phrase hits, significant terms for partial
/// hits, and the strong entity keys to look up in the entity index.
/// </summary>
public sealed class MemoryQuery
{
    private const int MinTermLength = 3;
    private const int MinPrefixLength = 4;

    // Question scaffolding, change-history and pending-work wording carry intent, not subject.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "these", "those", "from", "into", "about", "are", "was", "were",
        "been", "being", "have", "has", "had", "does", "did", "doing", "done", "can", "could", "should", "would",
        "will", "may", "might", "must", "not", "but", "any", "all", "our", "ours", "you", "your", "they", "them",
        "their", "its", "what", "when", "where", "which", "who", "why", "how", "there", "here", "then", "than",
        "also", "just", "some", "such", "use", "used", "using", "get", "set", "work", "works", "please", "tell",
        "show", "explain", "know", "changed", "change", "changes", "history", "timeline", "last", "time", "recently",
        "recent", "changelog", "before", "previously", "ago", "what's", "whats", "how's", "where's", "who's", "it's",
        "that's", "there's", "let's", "don't", "doesn't", "didn't", "isn't", "todo", "pending", "remaining", "left",
        "still", "need", "needs", "next", "step", "steps", "unresolved", "outstanding",
    };

    private MemoryQuery(string text, IReadOnlyList<string> terms, IReadOnlySet<string> entityKeys)
    {
        Text = text;
        Terms = terms;
        EntityKeys = entityKeys;
    }

    public string Text { get; }

    public IReadOnlyList<string> Terms { get; }

    public IReadOnlySet<string> EntityKeys { get; }

    /// <summary>False for a blank query or one made only of question and change-history words.</summary>
    public bool HasSubject => Terms.Count > 0 || EntityKeys.Count > 0;

    public static MemoryQuery Parse(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        var terms = TextTokens.Tokenize(trimmed)
            .Where(t => t.Length >= MinTermLength && !StopWords.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var entityKeys = EntityExtractor.StrongKeys(EntityExtractor.ExtractFromText(trimmed).Select(e => e.Key));

        return new MemoryQuery(trimmed, terms, entityKeys);
    }

    /// <summary>1 when the whole query appears in the memory's text, otherwise the fraction of
    /// <see cref="Terms"/> found (prefix-tolerant from <see cref="MinPrefixLength"/> characters).
    /// 0 when the query has no significant term.</summary>
    public double LexicalMatch(Context.Domain.Entities.Memory memory)
    {
        if (Terms.Count == 0)
        {
            return 0;
        }

        var fields = Fields(memory).ToList();
        if (fields.Any(f => f.Contains(Text, StringComparison.OrdinalIgnoreCase)))
        {
            return 1;
        }

        var tokens = fields.SelectMany(TextTokens.Tokenize).ToHashSet(StringComparer.Ordinal);
        var found = Terms.Count(term => tokens.Contains(term) || (term.Length >= MinPrefixLength && tokens.Any(token => IsPrefixMatch(term, token))));

        return (double)found / Terms.Count;
    }

    private static bool IsPrefixMatch(string term, string token) =>
        token.Length >= MinPrefixLength
        && (token.StartsWith(term, StringComparison.Ordinal) || term.StartsWith(token, StringComparison.Ordinal));

    private static IEnumerable<string> Fields(Context.Domain.Entities.Memory memory)
    {
        yield return memory.Title;
        yield return memory.Body;

        if (!string.IsNullOrWhiteSpace(memory.Topic))
        {
            yield return memory.Topic;
        }

        foreach (var json in (string?[])[memory.FactsJson, memory.ConceptsJson, memory.FilesReadJson, memory.FilesModifiedJson])
        {
            foreach (var value in EntityExtractor.ParseList(json))
            {
                yield return value;
            }
        }
    }
}
