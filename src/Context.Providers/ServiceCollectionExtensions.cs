using System.ClientModel;
using Anthropic.SDK;
using Context.Providers.Abstractions;
using Context.Providers.Embeddings;
using Context.Providers.Llm;
using Context.Providers.Reranking;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;
using OpenAI;
using OpenAI.Embeddings;

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

                    var clientOptions = new OpenAIClientOptions();
                    if (!string.IsNullOrWhiteSpace(o.BaseUrl))
                    {
                        clientOptions.Endpoint = new Uri(o.BaseUrl);
                    }

                    return new EmbeddingClient(o.Model, new ApiKeyCredential(o.ApiKey), clientOptions).AsIEmbeddingGenerator();
                });
                break;
            case "ollama":
                services.AddHttpClient("ollama-embeddings")
                    .AddHttpMessageHandler(sp => new OllamaHostFallbackHandler(
                        sp.GetRequiredService<IOptionsMonitor<OllamaOptions>>().Get(OllamaOptions.EmbeddingName).BaseUrl,
                        "Ollama embeddings", sp.GetService<ILoggerFactory>()?.CreateLogger<OllamaHostFallbackHandler>()));
                services.AddEmbeddingGenerator(sp =>
                {
                    var o = sp.GetRequiredService<IOptionsMonitor<OllamaOptions>>().Get(OllamaOptions.EmbeddingName);
                    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("ollama-embeddings");
                    http.BaseAddress ??= new Uri(o.BaseUrl.TrimEnd('/') + "/");
                    IEmbeddingGenerator<string, Embedding<float>> inner = new OllamaApiClient(http, o.Model);
                    return new OllamaEmbeddingGeneratorDecorator(inner, sp.GetService<ILogger<OllamaEmbeddingGeneratorDecorator>>());
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
            case "local-onnx":
            default:
                services.AddSingleton<IRerankerProvider, LocalOnnxReranker>();
                break;
        }

        switch (options.LlmProvider.Trim().ToLowerInvariant())
        {
            case "anthropic":
                services.AddChatClient(sp =>
                {
                    var o = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
                    if (string.IsNullOrWhiteSpace(o.ApiKey))
                    {
                        throw new InvalidOperationException(
                            "AnthropicLlmProvider requires an API key. Configure AnthropicOptions.ApiKey " +
                            "(e.g. sourced from the ANTHROPIC_API_KEY environment variable) before use.");
                    }

                    var http = new HttpClient();
                    if (!string.IsNullOrWhiteSpace(o.BaseUrl))
                    {
                        http.BaseAddress = new Uri(o.BaseUrl.TrimEnd('/') + "/");
                    }

                    return new AnthropicClient(new APIAuthentication(o.ApiKey), http).Messages;
                });
                break;
            case "ollama":
                services.AddHttpClient("ollama-chat")
                    .AddHttpMessageHandler(sp => new OllamaHostFallbackHandler(
                        sp.GetRequiredService<IOptionsMonitor<OllamaOptions>>().Get(OllamaOptions.ChatName).BaseUrl,
                        "Ollama chat", sp.GetService<ILoggerFactory>()?.CreateLogger<OllamaHostFallbackHandler>()));
                services.AddChatClient(sp =>
                {
                    var o = sp.GetRequiredService<IOptionsMonitor<OllamaOptions>>().Get(OllamaOptions.ChatName);
                    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("ollama-chat");
                    http.Timeout = TimeSpan.FromMinutes(5);
                    http.BaseAddress ??= new Uri(o.BaseUrl.TrimEnd('/') + "/");
                    IChatClient inner = new OllamaApiClient(http, o.Model);
                    return new OllamaChatClientDecorator(inner, o.Model);
                });
                break;
            default:
                services.AddChatClient(_ => new NoOpChatClient());
                break;
        }

        return services;
    }
}
