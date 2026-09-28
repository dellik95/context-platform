using System.Text;

namespace Context.Memory.Consolidation;

/// <summary>Rewrites two complementary memories' bodies into one, keeping every fact.</summary>
public static class MemoryMergePrompt
{
    public const string SystemPrompt =
        """
        You merge two engineering memories about the same subject in a code repository into one BODY.
        Keep every fact listed under FACTS and every concrete name (file paths, classes, methods, config
        keys, tickets) from both bodies. Describe current behavior in the present tense. Do not add
        anything that is not in the input. Respond with the merged body text only: no heading, no
        list of facts, no commentary.
        """;

    public static string BuildUserPrompt(RelationSubject existing, RelationSubject incoming, IReadOnlyList<string> facts, int maxLength)
    {
        var sb = new StringBuilder();
        sb.Append("TITLE: ").AppendLine(existing.Title);
        sb.AppendLine($"Keep the merged body under {maxLength} characters.");
        sb.AppendLine();
        sb.AppendLine("FACTS:");
        foreach (var fact in facts)
        {
            sb.Append("- ").AppendLine(fact);
        }

        sb.AppendLine();
        sb.AppendLine("BODY 1:");
        sb.AppendLine(existing.Body.Trim());
        sb.AppendLine();
        sb.AppendLine("BODY 2:");
        sb.AppendLine(incoming.Body.Trim());
        return sb.ToString();
    }

    /// <summary>The model's body trimmed to <paramref name="maxLength"/>, or null when it returned nothing usable.</summary>
    public static string? Clean(string? response, int maxLength)
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

        return text.Length == 0 ? null : Trim(text, maxLength);
    }

    /// <summary>The no-LLM merge: both bodies, the second dropped when the first already contains it.</summary>
    public static string Fallback(string existingBody, string incomingBody, int maxLength)
    {
        var existing = existingBody.Trim();
        var incoming = incomingBody.Trim();
        var merged = incoming.Length == 0 || TextTokens.Normalize(existing).Contains(TextTokens.Normalize(incoming), StringComparison.Ordinal)
            ? existing
            : $"{existing}\n\n{incoming}";
        return Trim(merged, maxLength);
    }

    private static string Trim(string text, int maxLength) => text.Length <= maxLength ? text : text[..maxLength].TrimEnd();
}
