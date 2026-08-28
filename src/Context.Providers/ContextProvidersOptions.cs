using Context.Providers.Embeddings;
using Context.Providers.Llm;
using Context.Providers.Reranking;

namespace Context.Providers;

/// <summary>
/// Simple config surface consumed by <see cref="ServiceCollectionExtensions.AddContextProviders"/>.
/// String selector properties pick which implementation of each provider category to register;
/// everything defaults to the offline/local variants so the platform runs with zero external
/// dependencies out of the box.
/// </summary>
public sealed record ContextProvidersOptions
{
    /// <summary>"noop" (default), "openai", or "ollama".</summary>
    public string EmbeddingProvider { get; init; } = "noop";

    /// <summary>"local-onnx" (default, heuristic fallback) or "noop".</summary>
    public string RerankerProvider { get; init; } = "local-onnx";

    /// <summary>"noop" (default) or "anthropic".</summary>
    public string LlmProvider { get; init; } = "noop";

    public OpenAiOptions OpenAi { get; init; } = new();

    public OllamaOptions Ollama { get; init; } = new();

    public LocalOnnxRerankerOptions LocalOnnx { get; init; } = new();

    public AnthropicOptions Anthropic { get; init; } = new();
}
