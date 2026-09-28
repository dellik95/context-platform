using System.Text;
using System.Text.RegularExpressions;

namespace Context.Memory.Consolidation;

/// <summary>
/// Classifies how a candidate relates to its nearest existing memories: one LLM call over up to five
/// neighbours, or an offline heuristic when no chat model is available. Unanswered neighbours are
/// Unrelated, so a sloppy response can only fail toward keeping memories apart.
/// </summary>
public static partial class MemoryRelationJudge
{
    public const int MaxBodyExcerpt = 600;
    public const double OfflineDuplicateCosine = 0.92;
    public const double OfflineComplementsCosine = 0.80;
    public const double NegationFactJaccard = 0.6;

    public const string SystemPrompt =
        """
        You compare one NEW engineering memory about a code repository with EXISTING memories from the
        same repository and classify how the new memory relates to each existing one.

        Verdicts:
        - DUPLICATE: states the same knowledge as the existing memory, possibly in other words; nothing
          important is new.
        - COMPLEMENTS: about the same specific subject (the same component, behavior or setting) and adds
          compatible facts; both remain true together.
        - SUPERSEDES: about the same specific subject; the new memory describes the current state and the
          existing one an older state that is no longer true (a changed value, a replaced mechanism).
        - CONTRADICTS: about the same specific subject; both cannot be true, and nothing shows which one
          is current.
        - UNRELATED: about a different subject.

        Sharing a repository, file, directory, technology, ticket or topic does not make memories related:
        two memories about different settings in the same file, or different problems in the same area,
        are UNRELATED. Use DUPLICATE, COMPLEMENTS, SUPERSEDES or CONTRADICTS only when both memories are
        about the same specific thing. When unsure, answer UNRELATED.

        Respond with exactly one line per existing memory and nothing else, in this format:
        <index>|<VERDICT>|<short reason>
        """;

    private static readonly HashSet<string> Negations = new(StringComparer.Ordinal)
    {
        "not", "no", "never", "without", "cannot", "none", "nor",
    };

    public static string BuildUserPrompt(RelationSubject candidate, IReadOnlyList<RelationSubject> neighbours)
    {
        var sb = new StringBuilder();
        AppendSubject(sb, "NEW", candidate);
        for (var i = 0; i < neighbours.Count; i++)
        {
            sb.AppendLine();
            AppendSubject(sb, $"EXISTING {i + 1}", neighbours[i]);
        }

        return sb.ToString();
    }

    /// <summary>One verdict per neighbour, in neighbour order. The first line for an index wins; a
    /// missing, out-of-range or unreadable line leaves that neighbour Unrelated.</summary>
    public static IReadOnlyList<RelationVerdict> Parse(string? response, int neighbourCount)
    {
        var verdicts = Enumerable.Repeat<RelationVerdict?>(null, neighbourCount).ToArray();
        foreach (var line in (response ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var match = VerdictLineRegex().Match(line);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var index) || index < 1 || index > neighbourCount
                || verdicts[index - 1] is not null)
            {
                continue;
            }

            if (ParseRelation(match.Groups[2].Value) is { } relation)
            {
                var reason = match.Groups[3].Success ? match.Groups[3].Value.Trim() : null;
                verdicts[index - 1] = new RelationVerdict(relation, string.IsNullOrEmpty(reason) ? null : reason);
            }
        }

        return [.. verdicts.Select(v => v ?? RelationVerdict.Unrelated)];
    }

    /// <summary>
    /// The no-LLM verdict. Never Supersedes: recency alone cannot tell a correction from a mistake.
    /// </summary>
    public static MemoryRelation JudgeOffline(RelationSignals signals)
    {
        if (signals.NegationAsymmetry)
        {
            return MemoryRelation.Contradicts;
        }

        if (signals.Cosine >= OfflineDuplicateCosine && signals.SharesStrongEntity)
        {
            return MemoryRelation.Duplicate;
        }

        return signals.Cosine >= OfflineComplementsCosine && signals.SharesStrongEntity && signals.SameType && signals.SameTopicKey
            ? MemoryRelation.Complements
            : MemoryRelation.Unrelated;
    }

    /// <summary>True when some fact of one side is the negation of a fact on the other: the same words
    /// (token Jaccard at or above <see cref="NegationFactJaccard"/>, negations ignored) with exactly one
    /// side negated. Titles stand in for a side without facts.</summary>
    public static bool HasNegationAsymmetry(RelationSubject a, RelationSubject b)
    {
        var left = Statements(a);
        var right = Statements(b);
        foreach (var (leftWords, leftNegated) in left)
        {
            foreach (var (rightWords, rightNegated) in right)
            {
                if (leftNegated != rightNegated && TextTokens.Jaccard(leftWords, rightWords) >= NegationFactJaccard)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static List<(HashSet<string> Words, bool Negated)> Statements(RelationSubject subject)
    {
        IReadOnlyList<string> sentences = subject.Facts.Count > 0 ? subject.Facts : [subject.Title];
        var statements = new List<(HashSet<string>, bool)>(sentences.Count);
        foreach (var sentence in sentences)
        {
            var words = new HashSet<string>(StringComparer.Ordinal);
            var negated = false;
            foreach (var token in TextTokens.Tokenize(sentence))
            {
                if (Negations.Contains(token) || token.EndsWith("n't", StringComparison.Ordinal))
                {
                    negated = !negated;
                    continue;
                }

                words.Add(token);
            }

            if (words.Count > 0)
            {
                statements.Add((words, negated));
            }
        }

        return statements;
    }

    private static MemoryRelation? ParseRelation(string raw) => raw.Trim().ToUpperInvariant() switch
    {
        "DUPLICATE" => MemoryRelation.Duplicate,
        "COMPLEMENTS" or "COMPLEMENT" => MemoryRelation.Complements,
        "SUPERSEDES" or "SUPERSEDE" => MemoryRelation.Supersedes,
        "CONTRADICTS" or "CONTRADICT" => MemoryRelation.Contradicts,
        "UNRELATED" => MemoryRelation.Unrelated,
        _ => null,
    };

    private static void AppendSubject(StringBuilder sb, string header, RelationSubject subject)
    {
        sb.AppendLine(header);
        sb.Append("TYPE: ").AppendLine(subject.Type.ToString());
        sb.Append("TITLE: ").AppendLine(subject.Title);
        if (subject.Facts.Count > 0)
        {
            sb.AppendLine("FACTS:");
            foreach (var fact in subject.Facts)
            {
                sb.Append("- ").AppendLine(fact);
            }
        }

        var body = subject.Body.Trim();
        sb.Append("BODY: ").AppendLine(body.Length <= MaxBodyExcerpt ? body : string.Concat(body.AsSpan(0, MaxBodyExcerpt), "…"));
    }

    [GeneratedRegex(@"^\s*\[?(\d+)\]?\s*\|\s*([A-Za-z_]+)\s*(?:\|(.*))?$")]
    private static partial Regex VerdictLineRegex();
}
