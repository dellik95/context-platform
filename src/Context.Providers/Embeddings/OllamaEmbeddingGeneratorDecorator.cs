using Microsoft.Extensions.AI;

namespace Context.Providers.Embeddings;

/// <summary>
/// Turns Ollama's "context length exceeded" rejection into an actionable error instead of retrying
/// with the prompt silently halved -- a truncated prompt produces an embedding for only part of the
/// chunk's content while still getting stored as if it represented the whole thing, corrupting
/// search results in a way that's invisible until someone notices bad matches. Failing loudly here
/// surfaces as a Failed job (see EmbedChunkJobHandler/the System tab's Failed jobs table) that names
/// the actual chunk, instead of a silent quality regression nobody gets to see.
/// </summary>
public sealed class OllamaEmbeddingGeneratorDecorator(IEmbeddingGenerator<string, Embedding<float>> inner, string model)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default)
    {
        try
        {
            return await base.GenerateAsync(values, options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IsContextLengthError(ex))
        {
            throw new InvalidOperationException(
                $"Ollama embeddings rejected this content as exceeding '{model}''s context window. " +
                "It needs to be split into smaller chunks upstream, or embedded with a model that has a larger window.",
                ex);
        }
    }

    private static bool IsContextLengthError(Exception ex) =>
        ex.Message.Contains("context length", StringComparison.OrdinalIgnoreCase);
}
