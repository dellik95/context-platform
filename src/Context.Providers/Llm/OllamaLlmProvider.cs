using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Context.Providers.Abstractions;

namespace Context.Providers.Llm;

/// <summary>
/// HTTP-based LLM provider for a local Ollama server (POST {BaseUrl}/api/chat, default
/// http://localhost:11434), used non-streaming. Lets session summarization and memory extraction
/// (Context.Memory) run on a real local model instead of falling back to
/// <see cref="NoOpLlmProvider"/>'s templated output — no API key, no data leaving the machine.
/// </summary>
public sealed class OllamaLlmProvider : ILlmProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly HttpClient _http;
    private readonly OllamaChatOptions _options;

    public OllamaLlmProvider(HttpClient httpClient, OllamaChatOptions options)
    {
        _http = httpClient;
        _options = options;
        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        }
    }

    public string Name => "ollama";

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat");
        request.Content = JsonContent.Create(
            new OllamaChatRequest(
                _options.Model,
                [new OllamaChatMessage("system", systemPrompt), new OllamaChatMessage("user", userPrompt)],
                Stream: false),
            options: JsonOptions);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
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
