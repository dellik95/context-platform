using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Context.Providers.Embeddings;

/// <summary>
/// Retries a single embedding request with the prompt halved each time Ollama rejects it for
/// exceeding the model's context length -- Ollama doesn't expose that limit up front, and
/// different models have different windows, so this is cheaper than guessing a token budget
/// per model. Capped so a truly pathological input still fails fast.
/// </summary>
public sealed class OllamaEmbeddingGeneratorDecorator(
    IEmbeddingGenerator<string, Embedding<float>> inner, ILogger? logger = null)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    private const int MaxContextLengthRetries = 5;
    private const int MinRetryTextLength = 64;

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default)
    {
        var results = new List<Embedding<float>>();
        foreach (var text in values)
        {
            results.Add(await GenerateOneAsync(text, options, ct).ConfigureAwait(false));
        }

        return new GeneratedEmbeddings<Embedding<float>>(results);
    }

    private async Task<Embedding<float>> GenerateOneAsync(string text, EmbeddingGenerationOptions? options, CancellationToken ct)
    {
        var prompt = text;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await base.GenerateAsync([prompt], options, ct).ConfigureAwait(false);
                return result[0];
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException
                && IsContextLengthError(ex)
                && attempt < MaxContextLengthRetries
                && prompt.Length > MinRetryTextLength)
            {
                prompt = prompt[..(prompt.Length / 2)];
                logger?.LogWarning(
                    "Ollama embeddings rejected a {OriginalLength}-char prompt as exceeding its context length; retrying attempt {Attempt} truncated to {TruncatedLength} chars.",
                    text.Length, attempt + 1, prompt.Length);
            }
        }
    }

    private static bool IsContextLengthError(Exception ex) =>
        ex.Message.Contains("context length", StringComparison.OrdinalIgnoreCase);
}
