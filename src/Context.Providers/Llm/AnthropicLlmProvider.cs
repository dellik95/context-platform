using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Context.Providers.Abstractions;

namespace Context.Providers.Llm;

/// <summary>
/// Real HTTP-based LLM provider calling the Anthropic Messages API
/// (POST {BaseUrl}/messages, default https://api.anthropic.com/v1/messages).
/// Registered as a typed client (see ServiceCollectionExtensions.AddContextProviders) so
/// <see cref="HttpClient"/> instances are pooled correctly.
/// </summary>
public sealed class AnthropicLlmProvider : ILlmProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly HttpClient _http;
    private readonly AnthropicOptions _options;

    public AnthropicLlmProvider(HttpClient httpClient, AnthropicOptions options)
    {
        _http = httpClient;
        _options = options;
        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/");
        }
    }

    public string Name => "anthropic";

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException(
                "AnthropicLlmProvider requires an API key. Configure AnthropicOptions.ApiKey " +
                "(e.g. sourced from the ANTHROPIC_API_KEY environment variable) before use.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "messages");
        request.Headers.Add("x-api-key", _options.ApiKey);
        request.Headers.Add("anthropic-version", _options.AnthropicVersion);
        request.Content = JsonContent.Create(
            new AnthropicMessageRequest(
                _options.Model,
                _options.MaxTokens,
                systemPrompt,
                [new AnthropicMessage("user", userPrompt)]),
            options: JsonOptions);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Anthropic messages request failed with status {(int)response.StatusCode}: {body}");
        }

        var parsed = JsonSerializer.Deserialize<AnthropicMessageResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("Anthropic messages response could not be parsed.");

        return string.Concat(
            parsed.Content.Where(c => c.Type == "text" && c.Text is not null).Select(c => c.Text));
    }

    private sealed record AnthropicMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record AnthropicMessageRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("messages")] AnthropicMessage[] Messages);

    private sealed record AnthropicContentBlock(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record AnthropicMessageResponse(
        [property: JsonPropertyName("content")] List<AnthropicContentBlock> Content);
}
