using Anthropic.SDK;
using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;

namespace Context.Providers.Llm;

/// <summary>
/// LLM provider for the Anthropic Messages API, built on Anthropic.SDK's <see cref="IChatClient"/>
/// implementation (Microsoft.Extensions.AI) instead of hand-rolled HTTP calls.
/// </summary>
public sealed class AnthropicLlmProvider : ILlmProvider
{
    private readonly IChatClient _chatClient;
    private readonly AnthropicOptions _options;

    public AnthropicLlmProvider(HttpClient httpClient, AnthropicOptions options)
    {
        _options = options;
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                "AnthropicLlmProvider requires an API key. Configure AnthropicOptions.ApiKey " +
                "(e.g. sourced from the ANTHROPIC_API_KEY environment variable) before use.");
        }

        if (httpClient.BaseAddress is null && !string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            httpClient.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        }

        _chatClient = new AnthropicClient(new APIAuthentication(options.ApiKey), httpClient).Messages;
    }

    public string Name => "anthropic";

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User, userPrompt),
        ];

        var response = await _chatClient
            .GetResponseAsync(messages, new ChatOptions { ModelId = _options.Model, MaxOutputTokens = _options.MaxTokens }, ct)
            .ConfigureAwait(false);

        return response.Text;
    }
}
