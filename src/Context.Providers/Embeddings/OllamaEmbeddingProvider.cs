using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Context.Providers.Abstractions;

namespace Context.Providers.Embeddings;

/// <summary>
/// HTTP-based embedding provider for a local Ollama server
/// (POST {BaseUrl}/api/embeddings, default http://localhost:11434). Ollama's legacy
/// embeddings endpoint accepts a single prompt per call, so batches are issued sequentially.
/// </summary>
public sealed class OllamaEmbeddingProvider : IEmbeddingProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly HttpClient _http;
    private readonly OllamaOptions _options;
    private int _dimensions;

    public OllamaEmbeddingProvider(HttpClient httpClient, OllamaOptions options)
    {
        _http = httpClient;
        _options = options;
        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        }

        _dimensions = options.Dimensions ?? 768;
    }

    public string Name => "ollama";

    public int Dimensions => _dimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/embeddings");
        request.Content = JsonContent.Create(new OllamaEmbedRequest(_options.Model, text), options: JsonOptions);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Ollama embeddings request failed with status {(int)response.StatusCode}: {body}");
        }

        var parsed = JsonSerializer.Deserialize<OllamaEmbedResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("Ollama embeddings response could not be parsed.");

        _dimensions = parsed.Embedding.Length;
        return parsed.Embedding;
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

    private sealed record OllamaEmbedRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("prompt")] string Prompt);

    private sealed record OllamaEmbedResponse(
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
