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
        You generate a concise, retrieval-oriented summary of a source code file for a codebase RAG search index.
        Input contains the file path and its top-level symbols, including their names, kinds, signatures, and documentation comments when available.
        Describe what this file is responsible for and why a developer might need to find it. Focus on the file's main purpose, domain concepts, important responsibilities, and significant behavior that can be reliably inferred from the provided information.
        Mention the most important methods or members only when they represent meaningful functionality. For each important member, briefly explain its purpose or role rather than repeating its signature. Do not try to describe every symbol.
        When supported by the input, include important architectural relationships, dependencies, patterns, or responsibilities that help distinguish this file from related files.
        Use documentation comments as authoritative information when available. Do not invent behavior, dependencies, business rules, data flows, or implementation details that cannot be inferred from the input.
        Prefer concrete technical and domain terminology that developers are likely to use when searching for this functionality. Include relevant concepts, operations, entities, services, handlers, repositories, APIs, or workflows when they are supported by the input.
        Avoid:

        * repeating the file path or obvious symbol names without adding useful information;
        * listing all classes, methods, or members;
        * repeating method signatures;
        * describing trivial getters, setters, fields, or constructors;
        * speculating about behavior not present in the input;
        * low-level implementation details that do not help identify the file;
        * generic statements such as "this file contains several methods and classes."

        Write 3–6 concise sentences. Make the description specific enough that a developer can determine whether this file is relevant to a task and identify which functionality is likely to be relevant without opening the file.
        Respond with ONLY the summary. No markdown, bullets, headings, or preamble.
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
