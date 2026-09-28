using Context.Application.Settings;
using Context.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Context.Application.Providers;

/// <summary>
/// Layers CONTEXT_FOUNDRY_* env vars under the saved <c>.claude/context-platform.local.json</c>
/// override, the same precedence <see cref="OllamaOptionsSetup"/> uses. <see cref="IConfiguration"/> is
/// optional because <see cref="ProjectProviderResolver"/> always resolves these options, including in
/// bare containers that register no configuration.
/// </summary>
public sealed class FoundryLocalOptionsSetup(ContextApplicationOptions applicationOptions, IConfiguration? configuration = null)
    : IConfigureOptions<FoundryLocalOptions>
{
    public void Configure(FoundryLocalOptions options)
    {
        var localSettings = LocalProjectSettingsReader.TryRead(applicationOptions.ProjectRoot);

        options.BaseUrl = FirstNonEmpty(localSettings?.FoundryBaseUrl, configuration?["CONTEXT_FOUNDRY_BASE_URL"]) ?? options.BaseUrl;
        options.EmbeddingModel = FirstNonEmpty(localSettings?.FoundryEmbeddingModel, configuration?["CONTEXT_FOUNDRY_EMBEDDING_MODEL"]) ?? options.EmbeddingModel;
        options.ChatModel = FirstNonEmpty(localSettings?.FoundryChatModel, configuration?["CONTEXT_FOUNDRY_CHAT_MODEL"]) ?? options.ChatModel;
    }

    private static string? FirstNonEmpty(string? first, string? second) =>
        first is { Length: > 0 } ? first : second is { Length: > 0 } ? second : null;
}
