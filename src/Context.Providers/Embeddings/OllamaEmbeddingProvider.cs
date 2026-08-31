using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;

namespace Context.Providers.Embeddings;

/// <summary>
/// Embedding provider for a local Ollama server, built on OllamaSharp's
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> implementation (Microsoft.Extensions.AI)
/// instead of hand-rolled HTTP calls. Host resolution (including the container fallback-host
/// retry) is delegated to <see cref="OllamaHostFallbackHandler"/>, attached to the HttpClient this
/// is constructed with (see ServiceCollectionExtensions), shared with <see cref="Llm.OllamaLlmProvider"/>.
/// </summary>
public sealed class OllamaEmbeddingProvider : IEmbeddingProvider
{
    // Different embedding models have different context windows (e.g. embeddinggemma vs.
    // nomic-embed-text), and Ollama doesn't expose it up front - so rather than guess a token
    // budget per model, an oversized chunk/file-summary is retried with the prompt halved each
    // time until it fits, capped so a truly pathological input still fails fast instead of
    // halving down to a near-empty (and therefore useless) embedding.
    private const int MaxContextLengthRetries = 5;
    private const int MinRetryTextLength = 64;

    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly OllamaOptions _options;
    private readonly ILogger _logger;
    private int _dimensions;

    public OllamaEmbeddingProvider(HttpClient httpClient, OllamaOptions options, ILogger<OllamaEmbeddingProvider>? logger = null)
    {
        _options = options;
        _logger = logger ?? NullLogger<OllamaEmbeddingProvider>.Instance;
        httpClient.BaseAddress ??= new Uri(options.BaseUrl.TrimEnd('/') + "/");
        _generator = new OllamaApiClient(httpClient, options.Model);
        _dimensions = options.Dimensions ?? 768;
    }

    public string Name => "ollama";

    public int Dimensions => _dimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var prompt = text;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var result = await _generator
                    .GenerateAsync(prompt, new EmbeddingGenerationOptions { ModelId = _options.Model }, ct)
                    .ConfigureAwait(false);

                var vector = result.Vector.ToArray();
                _dimensions = vector.Length;
                return vector;
            }
            catch (Exception ex) when (
                ex is not OperationCanceledException
                && IsContextLengthError(ex)
                && attempt < MaxContextLengthRetries
                && prompt.Length > MinRetryTextLength)
            {
                prompt = prompt[..(prompt.Length / 2)];
                _logger.LogWarning(
                    "Ollama embeddings model '{Model}' rejected a {OriginalLength}-char prompt as exceeding its context length; retrying attempt {Attempt} truncated to {TruncatedLength} chars.",
                    _options.Model, text.Length, attempt + 1, prompt.Length);
            }
        }
    }

    public async Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var results = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            results[i] = await EmbedAsync(texts[i], ct).ConfigureAwait(false);
        }

        return results;
    }

    private static bool IsContextLengthError(Exception ex) =>
        ex.Message.Contains("context length", StringComparison.OrdinalIgnoreCase);
}
