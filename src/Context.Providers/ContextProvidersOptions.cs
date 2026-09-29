namespace Context.Providers;

/// <summary>
/// Provider-<em>selection</em> surface consumed by <see cref="ServiceCollectionExtensions.AddContextProviders"/>.
/// String selector properties pick which implementation of each provider category to register;
/// everything defaults to the offline/local variants so the platform runs with zero external
/// dependencies out of the box. Per-provider settings (<see cref="OllamaOptions"/>, <see cref="FoundryLocalOptions"/>,
/// <see cref="Embeddings.OpenAiOptions"/>, <see cref="Llm.AnthropicOptions"/>) are configured
/// independently via the standard Options pattern, not nested here -- see
/// <c>Context.Application.Providers</c> for the setup classes that layer env vars/local overrides
/// into them, and <c>Context.Application.Providers.ContextProvidersOptionsResolver</c>'s remarks
/// for why *this* type's selector strings alone have to be resolved eagerly instead.
/// </summary>
public sealed record ContextProvidersOptions
{
    public const int DefaultLlmTimeoutSeconds = 1200;

    /// <summary>"noop" (default), "openai", "ollama", or "foundry" (Foundry Local).</summary>
    public string EmbeddingProvider { get; init; } = "noop";

    /// <summary>"lexical-overlap" (default, lexical token-overlap heuristic -- not ML-based) or "noop".</summary>
    public string RerankerProvider { get; init; } = "lexical-overlap";

    /// <summary>"noop" (default), "anthropic", "ollama", or "foundry" (Foundry Local).</summary>
    public string LlmProvider { get; init; } = "noop";

    public string? OpenAiApiKey { get; init; }

    public string? AnthropicApiKey { get; init; }

    /// <summary>HTTP timeout applied to every LLM/embedding call (Ollama, Anthropic, OpenAI) -- these
    /// can run long on a big prompt or a slow local model, well past the .NET HttpClient default of
    /// 100s. Configurable via CONTEXT_LLM_TIMEOUT_SECONDS.</summary>
    public int LlmTimeoutSeconds { get; init; } = DefaultLlmTimeoutSeconds;
}
