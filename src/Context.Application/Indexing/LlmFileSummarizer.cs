using System.Text;
using Context.Indexing.Abstractions;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Indexing;

/// <summary>
/// Default <see cref="IFileSummarizer"/>: asks the project's resolved <see cref="IChatClient"/>
/// (via <see cref="IProviderResolver"/>) for a retrieval-oriented description of a file, given its
/// path and top-level symbols (name, kind, signature, doc comment). Degrades to a no-op (returns
/// null) when there's no signal to summarize from (no symbols) or the provider is offline, matching
/// <c>LlmMemoryExtractor</c>'s own offline-provider convention.
/// </summary>
/// <remarks>
/// Deliberately asks for *more* than a one-line label (product feedback: search result
/// descriptions were too terse to be useful -- "should be extended, few sentences, methods, their
/// meaning, file responsibilities") -- a prior version of this prompt explicitly forbade
/// enumerating methods at all. This is the text persisted to <c>File.Summary</c> and chunked into
/// the search index (see <c>IndexingPipeline</c>'s <c>summaryChunk</c>), so its detail level
/// directly controls what a search hit's label/snippet looks like.
/// </remarks>
public sealed class LlmFileSummarizer(IProviderResolver providerResolver, ILogger<LlmFileSummarizer>? logger = null) : IFileSummarizer
{
    private const string SystemPrompt =
        """
        You generate a retrieval-oriented description of a source code file for a codebase RAG system.

        Given the file path and its top-level symbols (name, kind, signature, and doc comment where available), write a description covering:
        1. The file's primary responsibility and its role in the application/subsystem (1-2 sentences).
        2. Its most significant methods/functions/members: name each one that matters and say what it actually does and why it matters -- not just restating the signature. Skip trivial members (simple getters/setters, obvious constructors) unless they carry real behavior.
        3. Anything a developer would need to know before changing this file: important dependencies, invariants, or architectural role that can be reliably inferred from the input.

        Prefer meaningful domain and technical concepts that would help match developer questions to this file. Use the doc comments you're given as ground truth when present -- restate their meaning rather than inventing new claims.

        Do NOT:

        * restate a method's signature as if it were an explanation (e.g. "ComputeTotal takes a quantity and unit price and returns a decimal" restates the signature; "ComputeTotal multiplies quantity by unit price to get the line total" explains it);
        * enumerate every member indiscriminately -- pick the ones that convey the file's actual responsibility;
        * invent behavior, dependencies, or business rules not supported by the input.

        Target 3-6 sentences total. The description should let a developer decide whether this file is relevant to what they're working on, and roughly which method to look at, without opening it.

        Respond with ONLY the description. No preamble, markdown, bullets, or labels.

        """;

    private readonly ILogger<LlmFileSummarizer> _logger = logger ?? NullLogger<LlmFileSummarizer>.Instance;

    public async Task<string?> SummarizeAsync(Guid projectId, string relativePath, IReadOnlyList<SymbolDraft> symbols, CancellationToken ct)
    {
        if (symbols.Count == 0)
        {
            return null;
        }

        var chatClient = await providerResolver.GetChatClientAsync(projectId, ct).ConfigureAwait(false);
        if (chatClient.IsNoOp())
        {
            return null;
        }

        var userPrompt = $"File: {relativePath}\n{BuildSymbolList(symbols)}";
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

    // Caps: a pathological file with hundreds of top-level symbols (generated code, a huge enum)
    // shouldn't blow up the prompt -- MaxSymbolsListed keeps the request bounded, and each doc
    // comment is capped separately since a doc comment can itself be arbitrarily long.
    private const int MaxSymbolsListed = 40;
    private const int MaxDocCommentChars = 300;

    private static string BuildSymbolList(IReadOnlyList<SymbolDraft> symbols)
    {
        var sb = new StringBuilder("Top-level symbols:\n");
        foreach (var symbol in symbols.Take(MaxSymbolsListed))
        {
            var signature = string.IsNullOrWhiteSpace(symbol.SignatureText) ? symbol.Name : symbol.SignatureText;
            sb.Append("- [").Append(symbol.Kind).Append("] ").Append(signature);
            if (!string.IsNullOrWhiteSpace(symbol.DocComment))
            {
                var doc = symbol.DocComment.Length > MaxDocCommentChars
                    ? string.Concat(symbol.DocComment.AsSpan(0, MaxDocCommentChars), "…")
                    : symbol.DocComment;
                sb.Append(" — \"").Append(doc.Trim()).Append('"');
            }

            sb.Append('\n');
        }

        if (symbols.Count > MaxSymbolsListed)
        {
            sb.Append("(and ").Append(symbols.Count - MaxSymbolsListed).Append(" more symbols, omitted for brevity)\n");
        }

        return sb.ToString();
    }
}
