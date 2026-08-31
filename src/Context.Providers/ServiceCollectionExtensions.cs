using Context.Providers.Abstractions;
using Context.Providers.Embeddings;
using Context.Providers.Llm;
using Context.Providers.Reranking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Context.Providers;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IEmbeddingProvider"/>, <see cref="IRerankerProvider"/> and
    /// <see cref="ILlmProvider"/> based on the selector flags in <paramref name="options"/>.
    /// With no options supplied, everything defaults to the offline/local implementations
    /// (<see cref="NoOpEmbeddingProvider"/>, <see cref="LocalOnnxReranker"/> in heuristic-fallback
    /// mode, <see cref="NoOpLlmProvider"/>) so the pipeline works with zero external dependencies.
    /// </summary>
    public static IServiceCollection AddContextProviders(
        this IServiceCollection services,
        ContextProvidersOptions? options = null)
    {
        options ??= new ContextProvidersOptions();

        services.AddSingleton(Options.Create(options));
        services.AddSingleton(options.OpenAi);
        services.AddSingleton(options.Ollama);
        services.AddSingleton(options.OllamaChat);
        services.AddSingleton(options.LocalOnnx);
        services.AddSingleton(options.Anthropic);

        switch (options.EmbeddingProvider.Trim().ToLowerInvariant())
        {
            case "openai":
                services.AddSingleton<IEmbeddingProvider, OpenAiEmbeddingProvider>();
                break;
            case "ollama":
                services.AddHttpClient<IEmbeddingProvider, OllamaEmbeddingProvider>()
                    .AddHttpMessageHandler(sp => new OllamaHostFallbackHandler(
                        options.Ollama.BaseUrl, "Ollama embeddings", sp.GetService<ILoggerFactory>()?.CreateLogger<OllamaHostFallbackHandler>()));
                break;
            case "noop":
            default:
                services.AddSingleton<IEmbeddingProvider, NoOpEmbeddingProvider>();
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
                services.AddHttpClient<ILlmProvider, AnthropicLlmProvider>();
                break;
            case "ollama":
                services.AddHttpClient<ILlmProvider, OllamaLlmProvider>()
                    .AddHttpMessageHandler(sp => new OllamaHostFallbackHandler(
                        options.OllamaChat.BaseUrl, "Ollama chat", sp.GetService<ILoggerFactory>()?.CreateLogger<OllamaHostFallbackHandler>()));
                break;
            case "noop":
            default:
                services.AddSingleton<ILlmProvider, NoOpLlmProvider>();
                break;
        }

        return services;
    }
}
