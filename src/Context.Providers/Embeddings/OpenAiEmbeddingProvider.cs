using System.ClientModel;
using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Embeddings;

namespace Context.Providers.Embeddings;

/// <summary>
/// Embedding provider for the OpenAI embeddings API, built on the official OpenAI SDK's
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> adapter (Microsoft.Extensions.AI) instead
/// of hand-rolled HTTP calls.
/// </summary>
public sealed class OpenAiEmbeddingProvider : IEmbeddingProvider
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly OpenAiOptions _options;

    public OpenAiEmbeddingProvider(OpenAiOptions options)
    {
        _options = options;
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                "OpenAiEmbeddingProvider requires an API key. Configure OpenAiOptions.ApiKey " +
                "(e.g. sourced from the OPENAI_API_KEY environment variable) before use.");
        }

        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            clientOptions.Endpoint = new Uri(options.BaseUrl);
        }

        _generator = new EmbeddingClient(options.Model, new ApiKeyCredential(options.ApiKey), clientOptions)
            .AsIEmbeddingGenerator();
    }

    public string Name => "openai";

    public int Dimensions => _options.Model switch
    {
        "text-embedding-3-large" => 3072,
        "text-embedding-3-small" => 1536,
        "text-embedding-ada-002" => 1536,
        _ => 1536,
    };

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var result = await _generator.GenerateAsync(text, cancellationToken: ct).ConfigureAwait(false);
        return result.Vector.ToArray();
    }

    public async Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var result = await _generator.GenerateAsync(texts, cancellationToken: ct).ConfigureAwait(false);
        return [.. result.Select(e => e.Vector.ToArray())];
    }
}
