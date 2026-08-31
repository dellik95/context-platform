using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace Context.Providers.Llm;

/// <summary>
/// LLM provider for a local Ollama server, built on OllamaSharp's <see cref="IChatClient"/>
/// implementation (Microsoft.Extensions.AI) instead of hand-rolled HTTP calls. Lets session
/// summarization and memory extraction (Context.Memory) run on a real local model instead of
/// falling back to <see cref="NoOpLlmProvider"/>'s templated output — no API key, no data leaving
/// the machine. Host resolution (including the container fallback-host retry) is delegated to
/// <see cref="OllamaHostFallbackHandler"/>, attached to the HttpClient this is constructed with
/// (see ServiceCollectionExtensions), shared with <see cref="Embeddings.OllamaEmbeddingProvider"/>.
/// </summary>
public sealed class OllamaLlmProvider : ILlmProvider
{
    // HttpClient's own 100s default is not generous enough for a local CPU-bound model under any
    // concurrent load (several session summaries can legitimately queue up behind each other on
    // one Ollama instance - see SessionSummaryScheduler) - a slow real response should time out
    // far later than that, not get treated the same as a hung connection.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    private readonly IChatClient _chatClient;
    private readonly OllamaChatOptions _options;

    public OllamaLlmProvider(HttpClient httpClient, OllamaChatOptions options)
    {
        _options = options;
        httpClient.Timeout = RequestTimeout;
        httpClient.BaseAddress ??= new Uri(options.BaseUrl.TrimEnd('/') + "/");
        _chatClient = new OllamaApiClient(httpClient, options.Model);
    }

    public string Name => "ollama";

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct)
    {
        List<ChatMessage> messages =
        [
            new ChatMessage(ChatRole.System, systemPrompt),
            new ChatMessage(ChatRole.User, userPrompt),
        ];

        try
        {
            var response = await _chatClient
                .GetResponseAsync(messages, new ChatOptions { ModelId = _options.Model }, ct)
                .ConfigureAwait(false);

            return response.Text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Ollama chat request failed. Is Ollama running and has '{_options.Model}' been pulled ('ollama pull {_options.Model}')? {ex.Message}",
                ex);
        }
    }
}
