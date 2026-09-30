using System.Collections.Concurrent;
using Context.Application.Settings;
using Context.Providers;
using Context.Providers.Abstractions;
using Context.Providers.Embeddings;
using Context.Providers.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Context.Application.Providers;

/// <summary>
/// Default <see cref="IProviderResolver"/>: resolves each project's own saved
/// <see cref="ProjectSettingsDto"/> fresh on every call (no caching of the settings lookup — this
/// is what lets a Settings-tab save take effect immediately, no host restart needed) and caches the
/// CONSTRUCTED <see cref="IEmbeddingGenerator{String, Embedding}"/>/<see cref="IChatClient"/> by
/// its resolved (provider, baseUrl, model) tuple instead — two projects with identical Ollama
/// config legitimately share one client, and a project whose settings haven't changed reuses its
/// existing client rather than rebuilding one on every call.
/// <para/>
/// Registered as a Singleton (its consumers — <c>HybridSearchEngine</c>, <c>SqliteSearchIndex</c> —
/// already are, and cannot constructor-inject a Scoped dependency at all). <see cref="IProjectSettingsService"/>
/// is Scoped (backed by the pooled, Scoped <c>ContextDbContext</c>), so this resolves it from a
/// short-lived <see cref="IServiceScope"/> per call instead of constructor-injecting it directly —
/// the same pattern <c>SqliteSearchIndex</c>/<c>GraphPersistenceAdapter</c> already use for the
/// identical Scoped-dependency-from-a-Singleton problem, just via <see cref="IServiceScopeFactory"/>
/// instead of <c>IDbContextFactory</c>.
/// <para/>
/// OpenAI/Anthropic stay global, not per-project: <see cref="ProjectSettingsDto"/> carries no
/// API-key/model/base-URL fields for them, only Ollama's. A project that picks "openai"/"anthropic"
/// always uses the one process-wide key/model configured via <c>CONTEXT_OPENAI_API_KEY</c>/
/// <c>CONTEXT_ANTHROPIC_API_KEY</c> — only the provider *choice* and Ollama's/Foundry Local's own
/// base URL/models are genuinely per-project. An unset Foundry field falls back to the process-wide
/// <see cref="FoundryLocalOptions"/> (CONTEXT_FOUNDRY_*), since its port is machine-specific.
/// </summary>
public sealed class ProjectProviderResolver(
    IServiceScopeFactory scopeFactory,
    IOptions<AnthropicOptions> anthropicOptions,
    IOptions<OpenAiOptions> openAiOptions,
    IOptions<FoundryLocalOptions>? foundryOptions = null,
    ILoggerFactory? loggerFactory = null) : IProviderResolver
{
    private const string DefaultOllamaBaseUrl = "http://localhost:11434";

    private readonly ILoggerFactory _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    private readonly FoundryLocalOptions _foundryDefaults = foundryOptions?.Value ?? new FoundryLocalOptions();
    private readonly ConcurrentDictionary<string, Lazy<IEmbeddingGenerator<string, Embedding<float>>>> _embeddingCache = new();
    private readonly ConcurrentDictionary<string, Lazy<IChatClient>> _chatCache = new();

    public async Task<IEmbeddingGenerator<string, Embedding<float>>> GetEmbeddingGeneratorAsync(Guid projectId, CancellationToken ct)
    {
        var settings = await GetSettingsAsync(projectId, ct).ConfigureAwait(false);
        var provider = settings.EmbeddingProvider.Trim().ToLowerInvariant();
        var baseUrl = settings.OllamaBaseUrl is { Length: > 0 } configuredBaseUrl ? configuredBaseUrl : DefaultOllamaBaseUrl;
        var model = settings.OllamaEmbeddingModel is { Length: > 0 } configuredModel ? configuredModel : OllamaOptions.DefaultEmbeddingModel;
        var foundryBaseUrl = settings.FoundryBaseUrl is { Length: > 0 } configuredFoundryUrl ? configuredFoundryUrl : _foundryDefaults.BaseUrl;
        var foundryModel = settings.FoundryEmbeddingModel is { Length: > 0 } configuredFoundryModel ? configuredFoundryModel : _foundryDefaults.EmbeddingModel;
        var resilience = settings.ToResilienceOptions();
        var batchLimits = EmbeddingBatchLimits.ForProvider(provider).WithMaxInputs(settings.EmbeddingBatchSize);

        var key = provider switch
        {
            "ollama" => $"emb|ollama|{baseUrl}|{model}|{resilience}",
            "foundry" => $"emb|foundry|{foundryBaseUrl}|{foundryModel}|{resilience}",
            "openai" => $"emb|openai|{openAiOptions.Value.BaseUrl}|{openAiOptions.Value.Model}|{resilience}",
            _ => "emb|noop",
        } + $"|{batchLimits}";

        return _embeddingCache.GetOrAdd(key, _ => new Lazy<IEmbeddingGenerator<string, Embedding<float>>>(() => new BatchingEmbeddingGenerator(
            provider switch
            {
                "ollama" => ProviderFactory.CreateOllamaEmbeddingGenerator(baseUrl, model, resilience, _loggerFactory),
                "foundry" => ProviderFactory.CreateFoundryLocalEmbeddingGenerator(foundryBaseUrl, foundryModel, resilience),
                "openai" => ProviderFactory.CreateOpenAiEmbeddingGenerator(openAiOptions.Value, resilience, _loggerFactory),
                _ => new NoOpEmbeddingGenerator(),
            },
            batchLimits))).Value;
    }

    public async Task<IChatClient> GetChatClientAsync(Guid projectId, CancellationToken ct)
    {
        var settings = await GetSettingsAsync(projectId, ct).ConfigureAwait(false);
        var provider = settings.LlmProvider.Trim().ToLowerInvariant();
        var baseUrl = settings.OllamaBaseUrl is { Length: > 0 } configuredBaseUrl ? configuredBaseUrl : DefaultOllamaBaseUrl;
        var model = settings.OllamaChatModel is { Length: > 0 } configuredModel ? configuredModel : OllamaOptions.DefaultChatModel;
        var foundryBaseUrl = settings.FoundryBaseUrl is { Length: > 0 } configuredFoundryUrl ? configuredFoundryUrl : _foundryDefaults.BaseUrl;
        var foundryModel = settings.FoundryChatModel is { Length: > 0 } configuredFoundryModel ? configuredFoundryModel : _foundryDefaults.ChatModel;
        var resilience = settings.ToResilienceOptions();

        // Resilience is part of the key: the Ollama client carries its 429 cooldown state, so a
        // settings change gets a fresh client rather than mutating one other projects share.
        var key = provider switch
        {
            "ollama" => $"chat|ollama|{baseUrl}|{model}|{resilience}",
            "foundry" => $"chat|foundry|{foundryBaseUrl}|{foundryModel}|{resilience}",
            "anthropic" => $"chat|anthropic|{anthropicOptions.Value.BaseUrl}|{anthropicOptions.Value.Model}|{resilience}",
            _ => "chat|noop",
        };

        return _chatCache.GetOrAdd(key, _ => new Lazy<IChatClient>(() => provider switch
        {
            "ollama" => ProviderFactory.CreateOllamaChatClient(baseUrl, model, resilience, _loggerFactory),
            "foundry" => ProviderFactory.CreateFoundryLocalChatClient(foundryBaseUrl, foundryModel, resilience),
            "anthropic" => ProviderFactory.CreateAnthropicChatClient(anthropicOptions.Value, resilience, _loggerFactory),
            _ => new NoOpChatClient(),
        })).Value;
    }

    private async Task<ProjectSettingsDto> GetSettingsAsync(Guid projectId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var settingsService = scope.ServiceProvider.GetRequiredService<IProjectSettingsService>();
        return await settingsService.GetAsync(projectId, ct).ConfigureAwait(false);
    }
}
