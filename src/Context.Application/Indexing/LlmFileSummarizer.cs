using Context.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Indexing;

/// <summary>
/// Default <see cref="IFileSummarizer"/>: asks the configured <see cref="IChatClient"/> for a
/// short natural-language description of a file, given its path and top-level symbol names.
/// Degrades to a no-op (returns null) when there's no signal to summarize from (no symbols) or
/// the provider is offline, matching <c>LlmMemoryExtractor</c>'s own offline-provider convention.
/// </summary>
public sealed class LlmFileSummarizer(IChatClient chatClient, ILogger<LlmFileSummarizer>? logger = null) : IFileSummarizer
{
    private const string SystemPrompt =
        """
        You generate a retrieval-oriented description of a source code file for a codebase RAG system.

        Given the file path and its top-level symbols, describe the file's primary responsibility and its role in the application in 1–2 concise sentences. Focus on the domain concept, feature or subsystem, responsibilities, and important architectural role or dependencies that can be reliably inferred from the input.

        Prefer meaningful domain and technical concepts that would help match developer questions to this file. Mention specific operations or HTTP methods only when they clarify the file's responsibility.

        Do NOT:

        * restate the file name or symbol names unless they convey important meaning;
        * enumerate classes or methods;
        * describe individual methods one by one;
        * list HTTP verbs, parameters, or implementation details;
        * invent behavior, dependencies, or business rules not supported by the input.

        The description should answer: "Why would a developer need to look at this file?"

        Respond with ONLY the description. No preamble, markdown, bullets, or labels.
        
        """;

    private readonly ILogger<LlmFileSummarizer> _logger = logger ?? NullLogger<LlmFileSummarizer>.Instance;

    public async Task<string?> SummarizeAsync(string relativePath, IReadOnlyList<string> symbolNames, CancellationToken ct)
    {
        if (symbolNames.Count == 0 || chatClient.IsNoOp())
        {
            return null;
        }

        var userPrompt = $"File: {relativePath}\nTop-level symbols: {string.Join(", ", symbolNames)}";
        List<ChatMessage> messages = [new(ChatRole.System, SystemPrompt), new(ChatRole.User, userPrompt)];

        string response;
        try
        {
            var chatResponse = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            response = chatResponse.Text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "LLM provider {Provider} failed to summarize file {RelativePath}.", chatClient.GetProviderName(), relativePath);
            return null;
        }

        var trimmed = response.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
