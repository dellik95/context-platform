namespace Context.Providers;

/// <summary>
/// Provider-<em>selection</em> surface consumed by <see cref="ServiceCollectionExtensions.AddContextProviders"/>.
/// String selector properties pick which implementation of each provider category to register;
/// everything defaults to the offline/local variants so the platform runs with zero external
/// dependencies out of the box. Per-provider settings (<see cref="OllamaOptions"/>,
/// <see cref="Embeddings.OpenAiOptions"/>, <see cref="Llm.AnthropicOptions"/>) are configured
/// independently via the standard Options pattern, not nested here -- see
/// <c>Context.Application.Providers</c> for the setup classes that layer env vars/local overrides
/// into them, and <c>Context.Application.Providers.ContextProvidersOptionsResolver</c>'s remarks
/// for why *this* type's selector strings alone have to be resolved eagerly instead.
/// </summary>
public sealed record ContextProvidersOptions
{
    /// <summary>"noop" (default), "openai", or "ollama".</summary>
    public string EmbeddingProvider { get; init; } = "noop";

    /// <summary>"local-onnx" (default, heuristic fallback) or "noop".</summary>
    public string RerankerProvider { get; init; } = "local-onnx";

    /// <summary>"noop" (default), "anthropic", or "ollama".</summary>
    public string LlmProvider { get; init; } = "noop";

    public string? OpenAiApiKey { get; init; }

    public string? AnthropicApiKey { get; init; }
}
