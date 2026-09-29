using System.ClientModel;
using Anthropic.SDK;
using Context.Providers.Abstractions;
using Context.Providers.Embeddings;
using Context.Providers.Llm;
using Context.Providers.Reranking;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;
using OpenAI.Embeddings;
using Polly;

namespace Context.Providers;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddContextProviders(
        this IServiceCollection services,
        ContextProvidersOptions? options = null)
    {
        options ??= new ContextProvidersOptions();

        services.AddSingleton(Options.Create(options));
        services.Configure<AnthropicOptions>(o => o.ApiKey = options.AnthropicApiKey);
        services.Configure<OpenAiOptions>(o => o.ApiKey = options.OpenAiApiKey);

        var resilience = new ProviderResilienceOptions { Timeout = TimeSpan.FromSeconds(options.LlmTimeoutSeconds) };

        switch (options.EmbeddingProvider.Trim().ToLowerInvariant())
        {
            case "openai":
                services.AddEmbeddingGenerator(sp =>
                {
                    var o = sp.GetRequiredService<IOptions<OpenAiOptions>>().Value;
                    if (string.IsNullOrWhiteSpace(o.ApiKey))
                    {
                        throw new InvalidOperationException(
                            "OpenAiEmbeddingProvider requires an API key. Configure OpenAiOptions.ApiKey " +
                            "(e.g. sourced from the OPENAI_API_KEY environment variable) before use.");
                    }

                    var clientOptions = new OpenAIClientOptions { NetworkTimeout = resilience.Timeout, RetryPolicy = resilience.ToClientRetryPolicy() };
                    if (!string.IsNullOrWhiteSpace(o.BaseUrl))
                    {
                        clientOptions.Endpoint = new Uri(o.BaseUrl);
                    }

                    IEmbeddingGenerator<string, Embedding<float>> openAiGenerator =
                        new EmbeddingClient(o.Model, new ApiKeyCredential(o.ApiKey), clientOptions).AsIEmbeddingGenerator();
                    // BACKLOG-08 / SEC-4: openai is an external provider, so content is scanned for
                    // secret-shaped substrings and redacted before it leaves this machine.
                    return new SecretRedactingEmbeddingGeneratorDecorator(
                        openAiGenerator,
                        sp.GetRequiredService<ILogger<SecretRedactingEmbeddingGeneratorDecorator>>());
                });
                break;
            case "ollama":
                var embeddingsClient = services.AddHttpClient("ollama-embeddings");
                ConfigureLlmRetry(embeddingsClient, resilience);
                embeddingsClient.AddHttpMessageHandler(sp => new OllamaHostFallbackHandler(
                    sp.GetRequiredService<IOptionsMonitor<OllamaOptions>>().Get(OllamaOptions.EmbeddingName).BaseUrl,
                    "Ollama embeddings", sp.GetService<ILoggerFactory>()?.CreateLogger<OllamaHostFallbackHandler>()));
                services.AddEmbeddingGenerator(sp =>
                {
                    var o = sp.GetRequiredService<IOptionsMonitor<OllamaOptions>>().Get(OllamaOptions.EmbeddingName);
                    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("ollama-embeddings");
                    http.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // the resilience pipeline above is the sole timeout authority
                    http.BaseAddress ??= new Uri(o.BaseUrl.TrimEnd('/') + "/");
                    // Not something a generic retry handler can do: turns "content too long for this
                    // model's context window" into an actionable error instead of a retry decision.
                    IEmbeddingGenerator<string, Embedding<float>> inner = new OllamaApiClient(http, o.Model);
                    return new OllamaEmbeddingGeneratorDecorator(inner, o.Model);
                });
                break;
            case "foundry":
                ConfigureLlmRetry(services.AddHttpClient("foundry-embeddings"), resilience);
                services.AddEmbeddingGenerator(sp =>
                {
                    var o = sp.GetRequiredService<IOptions<FoundryLocalOptions>>().Value;
                    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("foundry-embeddings");
                    http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                    http.BaseAddress = new Uri(FoundryLocalOptions.ApiBaseUrl(o.BaseUrl) + "/");
                    return new FoundryLocalEmbeddingGenerator(http, o.EmbeddingModel);
                });
                break;
            default:
                services.AddEmbeddingGenerator(_ => new NoOpEmbeddingGenerator());
                break;
        }

        switch (options.RerankerProvider.Trim().ToLowerInvariant())
        {
            case "noop":
                services.AddSingleton<IRerankerProvider, NoOpReranker>();
                break;
            case "lexical-overlap":
            default:
                services.AddSingleton<IRerankerProvider, LexicalOverlapReranker>();
                break;
        }

        switch (options.LlmProvider.Trim().ToLowerInvariant())
        {
            case "anthropic":
                var anthropicClient = services.AddHttpClient("anthropic-chat");
                ConfigureLlmRetry(anthropicClient, resilience);
                services.AddChatClient(sp =>
                {
                    var o = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
                    if (string.IsNullOrWhiteSpace(o.ApiKey))
                    {
                        throw new InvalidOperationException(
                            "AnthropicLlmProvider requires an API key. Configure AnthropicOptions.ApiKey " +
                            "(e.g. sourced from the ANTHROPIC_API_KEY environment variable) before use.");
                    }

                    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("anthropic-chat");
                    http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                    if (!string.IsNullOrWhiteSpace(o.BaseUrl))
                    {
                        http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
                    }

                    IChatClient anthropicChatClient = new AnthropicClient(new APIAuthentication(o.ApiKey), http).Messages;
                    // BACKLOG-08 / SEC-4: anthropic is an external provider, so content is scanned
                    // for secret-shaped substrings and redacted before it leaves this machine.
                    return new SecretRedactingChatClientDecorator(
                        anthropicChatClient,
                        sp.GetRequiredService<ILogger<SecretRedactingChatClientDecorator>>());
                });
                break;
            case "ollama":
                var chatClient = services.AddHttpClient("ollama-chat");
                ConfigureLlmRetry(chatClient, resilience);
                chatClient.AddHttpMessageHandler(sp => new OllamaHostFallbackHandler(
                    sp.GetRequiredService<IOptionsMonitor<OllamaOptions>>().Get(OllamaOptions.ChatName).BaseUrl,
                    "Ollama chat", sp.GetService<ILoggerFactory>()?.CreateLogger<OllamaHostFallbackHandler>()));
                services.AddChatClient(sp =>
                {
                    var o = sp.GetRequiredService<IOptionsMonitor<OllamaOptions>>().Get(OllamaOptions.ChatName);
                    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("ollama-chat");
                    http.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                    http.BaseAddress ??= new Uri(o.BaseUrl.TrimEnd('/') + "/");
                    // Not something a generic retry handler can do: turns "the request threw" into an
                    // actionable hint ("have you pulled this model?"), not a retry decision.
                    IChatClient inner = new OllamaApiClient(http, o.Model);
                    return new OllamaChatClientDecorator(inner, o.Model, resilience);
                });
                break;
            case "foundry":
                services.AddChatClient(sp =>
                {
                    var o = sp.GetRequiredService<IOptions<FoundryLocalOptions>>().Value;
                    return ProviderFactory.CreateFoundryLocalChatClient(o.BaseUrl, o.ChatModel, resilience);
                });
                break;
            default:
                services.AddChatClient(_ => new NoOpChatClient());
                break;
        }

        return services;
    }

    /// <summary>
    /// An overall timeout plus the optional retry from <see cref="ProviderResilienceOptions"/> --
    /// deliberately NOT <c>AddStandardResilienceHandler</c>, which also bundles a circuit breaker and
    /// rate limiter. Neither fits here: this is a single local process talking to one self-hosted or
    /// third-party endpoint, and a circuit breaker would start hard-failing requests during exactly
    /// the kind of "Ollama is still loading the model" stretch a caller should instead wait out.
    /// The timeout is added first so it bounds the whole call (all retries + backoff combined).
    /// </summary>
    private static void ConfigureLlmRetry(IHttpClientBuilder builder, ProviderResilienceOptions resilience) =>
        builder.AddResilienceHandler("llm-retry", pipeline =>
        {
            pipeline.AddTimeout(resilience.Timeout);
            if (resilience.MaxRetries > 0)
            {
                pipeline.AddRetry(resilience.ToHttpRetryStrategy());
            }
        });
}
