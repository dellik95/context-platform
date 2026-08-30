using System.Text.Json;
using Context.Infrastructure.Abstractions;
using Context.Providers;
using Microsoft.Extensions.Options;

namespace Context.Application.Settings;

/// <summary>
/// Default <see cref="IProjectSettingsService"/> (CONTRACTS.md SS12). Ollama base-url/model are
/// left null in the seeded default: Context.Providers models embedding and chat Ollama config as
/// two independent option classes (Embeddings.OllamaOptions, Llm.OllamaChatOptions), each with its
/// own BaseUrl that can legitimately point at different servers - there is no single value that
/// correctly represents "the" Ollama base URL to seed the UI with, so defaulting to either one's
/// value would be misleading rather than merely incomplete.
/// </summary>
public sealed class ProjectSettingsService(
    IProjectRepository projectRepository,
    IOptions<ContextProvidersOptions> providersOptions) : IProjectSettingsService
{
    public async Task<ProjectSettingsDto> GetAsync(Guid projectId, CancellationToken ct)
    {
        var project = await projectRepository.GetByIdAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Project {projectId} was not found.");

        if (!string.IsNullOrEmpty(project.ConfigJson))
        {
            var saved = TryDeserialize(project.ConfigJson);
            if (saved is not null)
            {
                return saved;
            }
        }

        var options = providersOptions.Value;
        return new ProjectSettingsDto(
            options.EmbeddingProvider,
            options.LlmProvider,
            OllamaBaseUrl: null,
            OllamaEmbeddingModel: null,
            OllamaChatModel: null,
            ReindexEnabled: false,
            ReindexIntervalMinutes: 60,
            LastReindexScheduledAt: null);
    }

    public async Task UpdateAsync(Guid projectId, ProjectSettingsDto settings, CancellationToken ct)
    {
        var project = await projectRepository.GetByIdAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Project {projectId} was not found.");

        project.ConfigJson = JsonSerializer.Serialize(settings);
        await projectRepository.UpdateAsync(project, ct).ConfigureAwait(false);
    }

    // A malformed/legacy ConfigJson must fall back to defaults rather than fail GetAsync outright.
    private static ProjectSettingsDto? TryDeserialize(string configJson)
    {
        try
        {
            return JsonSerializer.Deserialize<ProjectSettingsDto>(configJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
