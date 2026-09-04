using Context.Application.Settings;
using Context.Providers;
using Microsoft.Extensions.Configuration;

namespace Context.Application.Providers;

/// <summary>
/// Resolves <see cref="ContextProvidersOptions"/>' provider-<em>selection</em> strings
/// (noop/ollama/openai/anthropic) at startup, before the DI container is built.
/// </summary>
/// <remarks>
/// This can't be a lazy <c>IConfigureOptions&lt;T&gt;</c> like every other provider options class
/// here: <c>Context.Providers.ServiceCollectionExtensions.AddContextProviders</c> uses these
/// strings to decide which CONCRETE TYPE to register for <c>IEmbeddingGenerator</c>/<c>IChatClient</c>
/// (e.g. Ollama's vs. <c>NoOpEmbeddingGenerator</c>), and that registration
/// decision has to happen before <c>WebApplicationBuilder.Build()</c> -- by which point no
/// <c>IOptions&lt;T&gt;</c> can be resolved yet. Swapping the provider *type* at runtime without a
/// restart would need a routing/keyed-service redesign (see ROADMAP.md's "Live settings reload --
/// provider selection hot-swap"); this resolver only covers what's achievable today.
/// </remarks>
public static class ContextProvidersOptionsResolver
{
    public static ContextProvidersOptions Resolve(IConfiguration configuration, string projectRoot)
    {
        // The Dashboard Settings tab's saved local override always wins over CONTEXT_* env vars --
        // this is what "Provider changes require a Context.Dashboard.Server restart" refers to (a restart is
        // still needed for the *type* switch above, but the Dashboard save takes effect on it).
        var localSettings = LocalProjectSettingsReader.TryRead(projectRoot);

        return new ContextProvidersOptions
        {
            EmbeddingProvider = localSettings?.EmbeddingProvider is { Length: > 0 } localEmbedding
                ? localEmbedding
                : configuration["CONTEXT_EMBEDDING_PROVIDER"] is { Length: > 0 } envEmbedding ? envEmbedding : "noop",
            LlmProvider = localSettings?.LlmProvider is { Length: > 0 } localLlm
                ? localLlm
                : configuration["CONTEXT_LLM_PROVIDER"] is { Length: > 0 } envLlm ? envLlm : "noop",
            OpenAiApiKey = configuration["CONTEXT_OPENAI_API_KEY"],
            AnthropicApiKey = configuration["CONTEXT_ANTHROPIC_API_KEY"],
            LlmTimeoutSeconds = int.TryParse(configuration["CONTEXT_LLM_TIMEOUT_SECONDS"], out var timeoutSeconds) && timeoutSeconds > 0
                ? timeoutSeconds
                : ContextProvidersOptions.DefaultLlmTimeoutSeconds,
        };
    }
}
