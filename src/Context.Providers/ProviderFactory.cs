using System.ClientModel;
using Anthropic.SDK;
using Context.Providers.Embeddings;
using Context.Providers.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OllamaSharp;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Embeddings;

namespace Context.Providers;

/// <summary>
/// Builds a standalone, long-lived <see cref="IEmbeddingGenerator{String, Embedding}"/>/
/// <see cref="IChatClient"/> for one resolved provider config — used by <c>Context.Application.
/// Providers.ProjectProviderResolver</c>, which caches the result per distinct config and reuses it
/// across every project that resolves to the same one.
/// <para/>
/// Deliberately independent of <c>ServiceCollectionExtensions.AddContextProviders</c>'s own
/// DI-registration-time construction (the single-project default path): that
/// path bakes <see cref="OllamaHostFallbackHandler"/> onto ONE shared named <c>HttpClient</c> with
/// a base URL fixed at DI-registration time — correct there, since that path only ever serves one
/// known-fixed project, but <see cref="OllamaHostFallbackHandler.SendAsync"/> rewrites every
/// request's host to that ONE fixed URL, which would silently misroute every project's Ollama calls
/// to the same host if that same registration were reused across projects with different
/// <c>OllamaBaseUrl</c> settings. Each distinct (provider, baseUrl, model) resolved here therefore
/// gets its own dedicated <see cref="HttpClient"/> scoped to that base URL — a small, bounded number
/// of long-lived clients (cardinality = distinct configs across all projects, not per call), the
/// same "few long-lived HttpClient instances" pattern <c>IHttpClientFactory</c> itself exists to
/// avoid needing, for exactly this kind of small, known endpoint set.
/// <para/>
/// One accepted behavioral difference from the DI-registration path: no Polly retry pipeline here
/// (that pipeline is built via <c>IHttpClientBuilder.AddResilienceHandler</c>, a DI-registration-time-
/// only API) — just a plain per-call <see cref="HttpClient.Timeout"/>. Acceptable for a per-project
/// resolver serving a local-first, mostly-local-Ollama use case; the shared default path keeps full
/// retry/timeout resilience unchanged.
/// </summary>
public static class ProviderFactory
{
    public static IEmbeddingGenerator<string, Embedding<float>> CreateOllamaEmbeddingGenerator(
        string baseUrl, string model, TimeSpan timeout, ILoggerFactory loggerFactory)
    {
        var http = CreateOllamaHttpClient(baseUrl, "Ollama embeddings", timeout, loggerFactory);
        IEmbeddingGenerator<string, Embedding<float>> inner = new OllamaApiClient(http, model);
        return new OllamaEmbeddingGeneratorDecorator(inner, model);
    }

    public static IEmbeddingGenerator<string, Embedding<float>> CreateOpenAiEmbeddingGenerator(
        OpenAiOptions options, TimeSpan timeout, ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                "OpenAiEmbeddingProvider requires an API key. Configure OpenAiOptions.ApiKey " +
                "(e.g. sourced from the OPENAI_API_KEY environment variable) before use.");
        }

        var clientOptions = new OpenAIClientOptions { NetworkTimeout = timeout };
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            clientOptions.Endpoint = new Uri(options.BaseUrl);
        }

        IEmbeddingGenerator<string, Embedding<float>> openAiGenerator =
            new EmbeddingClient(options.Model, new ApiKeyCredential(options.ApiKey), clientOptions).AsIEmbeddingGenerator();
        // BACKLOG-08 / SEC-4: openai is an external provider, so content is scanned for
        // secret-shaped substrings and redacted before it leaves this machine.
        return new SecretRedactingEmbeddingGeneratorDecorator(
            openAiGenerator, loggerFactory.CreateLogger<SecretRedactingEmbeddingGeneratorDecorator>());
    }

    public static IEmbeddingGenerator<string, Embedding<float>> CreateFoundryLocalEmbeddingGenerator(
        string baseUrl, string model, TimeSpan timeout) =>
        new FoundryLocalEmbeddingGenerator(
            new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = timeout },
            model);

    public static IChatClient CreateFoundryLocalChatClient(string baseUrl, string model, TimeSpan timeout)
    {
        var endpoint = new Uri(baseUrl.TrimEnd('/'));
        var chatClient = new ChatClient(
            model,
            new ApiKeyCredential(FoundryLocalOptions.PlaceholderApiKey),
            new OpenAIClientOptions { Endpoint = endpoint, NetworkTimeout = timeout });
        return new FoundryLocalChatClientDecorator(chatClient.AsIChatClient(), endpoint, model);
    }

    public static IChatClient CreateOllamaChatClient(string baseUrl, string model, TimeSpan timeout, ILoggerFactory loggerFactory)
    {
        var http = CreateOllamaHttpClient(baseUrl, "Ollama chat", timeout, loggerFactory);
        IChatClient inner = new OllamaApiClient(http, model);
        return new OllamaChatClientDecorator(inner, model);
    }

    public static IChatClient CreateAnthropicChatClient(AnthropicOptions options, TimeSpan timeout, ILoggerFactory loggerFactory)
    {
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                "AnthropicLlmProvider requires an API key. Configure AnthropicOptions.ApiKey " +
                "(e.g. sourced from the ANTHROPIC_API_KEY environment variable) before use.");
        }

        var http = new HttpClient { Timeout = timeout };
        if (!string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
        }

        IChatClient anthropicChatClient = new AnthropicClient(new APIAuthentication(options.ApiKey), http).Messages;
        // BACKLOG-08 / SEC-4: anthropic is an external provider, so content is scanned for
        // secret-shaped substrings and redacted before it leaves this machine.
        return new SecretRedactingChatClientDecorator(
            anthropicChatClient, loggerFactory.CreateLogger<SecretRedactingChatClientDecorator>());
    }

    private static HttpClient CreateOllamaHttpClient(string baseUrl, string label, TimeSpan timeout, ILoggerFactory loggerFactory)
    {
        var fallbackHandler = new OllamaHostFallbackHandler(baseUrl, label, loggerFactory.CreateLogger<OllamaHostFallbackHandler>())
        {
            InnerHandler = new SocketsHttpHandler(),
        };

        return new HttpClient(fallbackHandler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = timeout,
        };
    }
}
