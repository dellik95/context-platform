using System.Text;

namespace Context.Memory.Consolidation;

/// <summary>Rewrites complementary memories' bodies into one, keeping every fact. Never cuts text: a
/// merge that would not fit the size limit is not performed.</summary>
public static class MemoryMergePrompt
{
    public const string SystemPrompt =
        """
        You merge several engineering memories about the same subject in a code repository into one BODY.
        Keep every fact listed under FACTS and every concrete name (file paths, classes, methods, config
        keys, tickets) from all bodies. Describe current behavior in the present tense. Do not add
        anything that is not in the input. Respond with the merged body text only: no heading, no
        list of facts, no commentary.
        """;

    /// <summary><paramref name="subjects"/> are the memories to combine; the first one's title heads the prompt.</summary>
    public static string BuildUserPrompt(IReadOnlyList<RelationSubject> subjects, IReadOnlyList<string> facts, int maxLength)
    {
        var sb = new StringBuilder();
        sb.Append("TITLE: ").AppendLine(subjects[0].Title);
        sb.AppendLine($"Keep the merged body under {maxLength} characters.");
        sb.AppendLine();
        sb.AppendLine("FACTS:");
        foreach (var fact in facts)
        {
            sb.Append("- ").AppendLine(fact);
        }

        for (var i = 0; i < subjects.Count; i++)
        {
            sb.AppendLine();
            sb.Append("BODY ").Append(i + 1).AppendLine(":");
            sb.AppendLine(subjects[i].Body.Trim());
        }

        return sb.ToString();
    }

    /// <summary>The model's body, or null when it returned nothing usable.</summary>
    public static string? Clean(string? response)
    {
        var text = response?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (text.StartsWith("BODY:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[5..].Trim();
        }

        return text.Length == 0 ? null : text;
    }

    /// <summary>The no-LLM merge: every body in order, each one dropped when the text so far already contains it.</summary>
    public static string Fallback(IEnumerable<string> bodies)
    {
        var merged = new StringBuilder();
        var seen = "";
        foreach (var body in bodies.Select(b => b.Trim()).Where(b => b.Length > 0))
        {
            if (seen.Contains(TextTokens.Normalize(body), StringComparison.Ordinal))
            {
                continue;
            }

            if (merged.Length > 0)
            {
                merged.Append("\n\n");
            }

            merged.Append(body);
            seen = TextTokens.Normalize(merged.ToString());
        }

        return merged.ToString();
    }
}
