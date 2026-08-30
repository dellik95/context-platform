using Context.Providers.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Indexing;

/// <summary>
/// Default <see cref="IFileSummarizer"/>: asks the configured <see cref="ILlmProvider"/> for a
/// short natural-language description of a file, given its path and top-level symbol names.
/// Degrades to a no-op (returns null) when there's no signal to summarize from (no symbols) or
/// the provider is offline, matching <c>LlmMemoryExtractor</c>'s own offline-provider convention.
/// </summary>
public sealed class LlmFileSummarizer(ILlmProvider llmProvider, ILogger<LlmFileSummarizer>? logger = null) : IFileSummarizer
{
    private const string SystemPrompt =
        "You write a single concise 1-2 sentence description of what a source code file is for, " +
        "given its file path and the names of its top-level symbols. Respond with ONLY the " +
        "description, no preamble, no markdown.";

    private readonly ILogger<LlmFileSummarizer> _logger = logger ?? NullLogger<LlmFileSummarizer>.Instance;

    public async Task<string?> SummarizeAsync(string relativePath, IReadOnlyList<string> symbolNames, CancellationToken ct)
    {
        if (symbolNames.Count == 0 || IsOffline(llmProvider))
        {
            return null;
        }

        var userPrompt = $"File: {relativePath}\nTop-level symbols: {string.Join(", ", symbolNames)}";

        string response;
        try
        {
            response = await llmProvider.CompleteAsync(SystemPrompt, userPrompt, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "LLM provider {Provider} failed to summarize file {RelativePath}.", llmProvider.Name, relativePath);
            return null;
        }

        var trimmed = response.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool IsOffline(ILlmProvider provider) =>
        string.Equals(provider.Name, "noop", StringComparison.OrdinalIgnoreCase);
}
