using Context.Application.Settings;
using Context.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Context.Application.Providers;

/// <summary>
/// Configures the <see cref="OllamaOptions.EmbeddingName"/> and <see cref="OllamaOptions.ChatName"/>
/// named <see cref="OllamaOptions"/> instances -- the embedding and chat models are almost always
/// different pulls on the same Ollama server, so they're modeled as two named instances of one
/// options type instead of two near-duplicate classes. Layers CONTEXT_OLLAMA_* env vars under the
/// Dashboard Settings tab's saved <c>.claude/context-platform.local.json</c> override, same
/// precedence <see cref="ContextProvidersOptionsResolver"/> uses for provider selection.
/// </summary>
public sealed class OllamaOptionsSetup(IConfiguration configuration, ContextApplicationOptions applicationOptions) : IConfigureNamedOptions<OllamaOptions>
{
    public void Configure(string? name, OllamaOptions options)
    {
        var localSettings = LocalProjectSettingsReader.TryRead(applicationOptions.ProjectRoot);

        options.BaseUrl = localSettings?.OllamaBaseUrl is { Length: > 0 } localBaseUrl
            ? localBaseUrl
            : configuration["CONTEXT_OLLAMA_BASE_URL"] is { Length: > 0 } envBaseUrl ? envBaseUrl : options.BaseUrl;

        options.Model = name switch
        {
            OllamaOptions.EmbeddingName => localSettings?.OllamaEmbeddingModel is { Length: > 0 } localEmbeddingModel
                ? localEmbeddingModel
                : configuration["CONTEXT_OLLAMA_EMBEDDING_MODEL"] is { Length: > 0 } envEmbeddingModel
                    ? envEmbeddingModel
                    : OllamaOptions.DefaultEmbeddingModel,
            OllamaOptions.ChatName => localSettings?.OllamaChatModel is { Length: > 0 } localChatModel
                ? localChatModel
                : configuration["CONTEXT_OLLAMA_CHAT_MODEL"] is { Length: > 0 } envChatModel
                    ? envChatModel
                    : OllamaOptions.DefaultChatModel,
            _ => options.Model,
        };
    }

    public void Configure(OllamaOptions options) => Configure(Options.DefaultName, options);
}
