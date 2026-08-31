using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Context.Providers.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Providers.Llm;

/// <summary>
/// HTTP-based LLM provider for a local Ollama server (POST {BaseUrl}/api/chat, default
/// http://localhost:11434), used non-streaming. Lets session summarization and memory extraction
/// (Context.Memory) run on a real local model instead of falling back to
/// <see cref="NoOpLlmProvider"/>'s templated output — no API key, no data leaving the machine.
/// Host resolution (including the container fallback-host retry) is delegated to
/// <see cref="OllamaHostResolver"/>, shared with <see cref="Embeddings.OllamaEmbeddingProvider"/>.
/// </summary>
public sealed class OllamaLlmProvider : ILlmProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly HttpClient _http;
    private readonly OllamaChatOptions _options;
    private readonly OllamaHostResolver _hostResolver;

    // HttpClient's own 100s default is not generous enough for a local CPU-bound model under any
    // concurrent load (several session summaries can legitimately queue up behind each other on
    // one Ollama instance - see SessionSummaryScheduler) - a slow real response should time out
    // far later than that, not get treated the same as a hung connection.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    public OllamaLlmProvider(HttpClient httpClient, OllamaChatOptions options, ILogger<OllamaLlmProvider>? logger = null)
    {
        _http = httpClient;
        _options = options;
        _http.Timeout = RequestTimeout;
        // No fixed BaseAddress: OllamaHostResolver builds the full URI per request so it can
        // retry against fallback hosts when running in a container (see its own remarks).
        _hostResolver = new OllamaHostResolver(_http, _options.BaseUrl, "Ollama chat", logger ?? NullLogger<OllamaLlmProvider>.Instance);
    }

    public string Name => "ollama";

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        using var response = await _hostResolver.SendAsync(
            HttpMethod.Post,
            "api/chat",
            () => JsonContent.Create(
                new OllamaChatRequest(
                    _options.Model,
                    [new OllamaChatMessage("system", systemPrompt), new OllamaChatMessage("user", userPrompt)],
                    Stream: false),
                options: JsonOptions),
            ct).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Ollama chat request failed with status {(int)response.StatusCode}: {body}. " +
                $"Is Ollama running and has '{_options.Model}' been pulled ('ollama pull {_options.Model}')?");
        }

        var parsed = JsonSerializer.Deserialize<OllamaChatResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("Ollama chat response could not be parsed.");

        return parsed.Message.Content;
    }

    private sealed record OllamaChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record OllamaChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] OllamaChatMessage[] Messages,
        [property: JsonPropertyName("stream")] bool Stream);

    private sealed record OllamaChatResponse(
        [property: JsonPropertyName("message")] OllamaChatMessage Message);
}
