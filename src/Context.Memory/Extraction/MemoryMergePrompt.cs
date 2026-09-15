namespace Context.Memory.Extraction;

/// <summary>
/// Prompts an LLM to merge several similar memory records into one consolidated memory, for
/// <see cref="Context.Application.Jobs.Handlers.CompactMemoryJobHandler"/>'s periodic compaction
/// pass. Public (unlike <see cref="MemoryConsistencyChecker"/>, which stays internal) because the
/// handler that calls it lives in the separate Context.Application assembly.
/// </summary>
public static class MemoryMergePrompt
{
    public const string SystemPrompt =
        """
        You merge several similar memory records about a software project into ONE consolidated
        memory. Preserve every distinct fact that appears in any source - do not drop anything
        that appears in only one source, even if most of the other sources don't mention it. You
        may remove redundancy between sources that restate the same fact, but never lose unique
        information.

        Respond in exactly this format:
        TITLE: <consolidated title>
        BODY: <consolidated body>
        """;

    public static string BuildUserPrompt(IReadOnlyList<Context.Domain.Entities.Memory> sources)
    {
        var blocks = sources.Select((source, index) => $"SOURCE {index + 1} ({source.Type}): {source.Title}\n{source.Body}");
        return string.Join("\n\n", blocks);
    }

    public static (string Title, string Body) ParseResponse(string llmResponse, IReadOnlyList<Context.Domain.Entities.Memory> sources)
    {
        try
        {
            string? title = null;
            System.Text.StringBuilder? body = null;

            foreach (var rawLine in llmResponse.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (title is null && line.StartsWith("TITLE:", StringComparison.OrdinalIgnoreCase))
                {
                    title = line["TITLE:".Length..].Trim();
                }
                else if (body is null && line.StartsWith("BODY:", StringComparison.OrdinalIgnoreCase))
                {
                    body = new System.Text.StringBuilder(line["BODY:".Length..].Trim());
                }
                else if (body is not null)
                {
                    body.Append('\n').Append(line);
                }
            }

            if (string.IsNullOrWhiteSpace(title) || body is null || string.IsNullOrWhiteSpace(body.ToString()))
            {
                return HeuristicMerge(sources);
            }

            return (title, body.ToString().Trim());
        }
        catch
        {
            return HeuristicMerge(sources);
        }
    }

    /// <summary>
    /// Deterministic fallback used when no real LLM is configured (<c>chatClient.IsNoOp()</c>) or
    /// the LLM call/parse otherwise fails: title from the highest-confidence source (ties keep the
    /// first, since <see cref="Enumerable.OrderByDescending"/> is a stable sort), body is every
    /// distinct source body joined together so no source's content is silently dropped.
    /// </summary>
    public static (string Title, string Body) HeuristicMerge(IReadOnlyList<Context.Domain.Entities.Memory> sources)
    {
        var title = sources.OrderByDescending(s => s.Confidence).First().Title;
        var body = string.Join("\n\n", sources.Select(s => s.Body).Distinct());
        return (title, body);
    }
}
