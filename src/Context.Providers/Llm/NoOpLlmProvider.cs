using Context.Providers.Abstractions;

namespace Context.Providers.Llm;

/// <summary>
/// Offline LLM provider used when no real LLM is configured. Produces a deterministic,
/// templated "summary" with no network call so session summarization and memory extraction
/// keep working end-to-end (at lower quality than a real model).
/// </summary>
public sealed class NoOpLlmProvider : ILlmProvider
{
    private const int MaxSystemPreview = 200;
    private const int MaxUserPreview = 1200;

    public string Name => "noop";

    public Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var text =
            "[No LLM provider configured — deterministic offline summary]" + Environment.NewLine +
            "System guidance: " + Truncate(systemPrompt, MaxSystemPreview) + Environment.NewLine +
            "Request:" + Environment.NewLine + Truncate(userPrompt, MaxUserPreview) + Environment.NewLine +
            "(Configure an LLM provider, e.g. Anthropic, for higher-quality summarization.)";

        return Task.FromResult(text);
    }

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(none)";
        }

        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}
