using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Context.Providers.Abstractions;

namespace Context.Providers.Embeddings;

/// <summary>
/// Real HTTP-based embedding provider calling the OpenAI embeddings REST endpoint
/// (POST {BaseUrl}/embeddings, default https://api.openai.com/v1/embeddings).
/// Registered as a typed client (see ServiceCollectionExtensions.AddContextProviders) so
/// <see cref="HttpClient"/> instances are pooled correctly.
/// </summary>
public sealed class OpenAiEmbeddingProvider : IEmbeddingProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly HttpClient _http;
    private readonly OpenAiOptions _options;

    public OpenAiEmbeddingProvider(HttpClient httpClient, OpenAiOptions options)
    {
        _http = httpClient;
        _options = options;
        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        }
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
        var batch = await EmbedBatchAsync([text], ct).ConfigureAwait(false);
        return batch[0];
    }

    public async Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        EnsureConfigured();

        using var request = new HttpRequestMessage(HttpMethod.Post, "embeddings");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        request.Content = JsonContent.Create(
            new OpenAiEmbeddingRequest(_options.Model, [.. texts]), options: JsonOptions);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenAI embeddings request failed with status {(int)response.StatusCode}: {body}");
        }

        var parsed = JsonSerializer.Deserialize<OpenAiEmbeddingResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("OpenAI embeddings response could not be parsed.");

        return [.. parsed.Data.OrderBy(d => d.Index).Select(d => d.Embedding)];
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException(
                "OpenAiEmbeddingProvider requires an API key. Configure OpenAiOptions.ApiKey " +
                "(e.g. sourced from the OPENAI_API_KEY environment variable) before use.");
        }
    }

    private sealed record OpenAiEmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string[] Input);

    private sealed record OpenAiEmbeddingDatum(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);

    private sealed record OpenAiEmbeddingResponse(
        [property: JsonPropertyName("data")] List<OpenAiEmbeddingDatum> Data);
}
