using System.Text.Json;
using System.Text.Json.Nodes;
using Context.Infrastructure.Abstractions;
using Context.Providers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Context.Application.Settings;

/// <summary>
/// Default <see cref="IProjectSettingsService"/> with layered settings resolution:
/// appsettings defaults -> project DB settings -> project-local settings file.
/// </summary>
public sealed class ProjectSettingsService(
    IProjectRepository projectRepository,
    IOptions<ContextProvidersOptions> providersOptions,
    IConfiguration? configuration = null) : IProjectSettingsService
{
    private const string DefaultsSection = "ContextPlatform:ProjectSettingsDefaults";

    public async Task<ProjectSettingsDto> GetAsync(Guid projectId, CancellationToken ct)
    {
        var project = await projectRepository.GetByIdAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Project {projectId} was not found.");

        var effective = CreateAppsettingsDefaults();

        if (!string.IsNullOrWhiteSpace(project.ConfigJson))
        {
            var databaseSettings = TryDeserialize(project.ConfigJson);
            if (databaseSettings is not null)
            {
                effective = databaseSettings;
            }
        }

        var localSettings = await LocalProjectSettingsReader.TryReadAsync(project.RootPath, ct).ConfigureAwait(false);
        if (localSettings is not null)
        {
            effective = localSettings;
        }

        return effective;
    }

    public async Task UpdateAsync(Guid projectId, ProjectSettingsDto settings, CancellationToken ct)
    {
        var project = await projectRepository.GetByIdAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Project {projectId} was not found.");

        project.ConfigJson = JsonSerializer.Serialize(settings);
        await projectRepository.UpdateAsync(project, ct).ConfigureAwait(false);
    }

    public async Task UpdateLocalAsync(Guid projectId, ProjectSettingsDto settings, CancellationToken ct)
    {
        var project = await projectRepository.GetByIdAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Project {projectId} was not found.");

        var configPath = Path.Combine(project.RootPath, ".claude", "context-platform.local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        JsonObject root;
        if (File.Exists(configPath))
        {
            var existing = await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false);
            root = JsonNode.Parse(existing) as JsonObject ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        root["settings"] = JsonSerializer.SerializeToNode(settings);
        var output = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(configPath, output, ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(Guid projectId, ProjectSettingsDto settings, ProjectSettingsWriteTarget target, CancellationToken ct)
    {
        switch (target)
        {
            case ProjectSettingsWriteTarget.Database:
                await UpdateAsync(projectId, settings, ct).ConfigureAwait(false);
                break;
            case ProjectSettingsWriteTarget.Local:
                await UpdateLocalAsync(projectId, settings, ct).ConfigureAwait(false);
                break;
            case ProjectSettingsWriteTarget.Both:
                await UpdateAsync(projectId, settings, ct).ConfigureAwait(false);
                await UpdateLocalAsync(projectId, settings, ct).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported settings write target.");
        }
    }

    private ProjectSettingsDto CreateAppsettingsDefaults()
    {
        var options = providersOptions.Value;
        var section = configuration?.GetSection(DefaultsSection);

        return new ProjectSettingsDto(
            EmbeddingProvider: section?["EmbeddingProvider"] is { Length: > 0 } embeddingProvider ? embeddingProvider : options.EmbeddingProvider,
            LlmProvider: section?["LlmProvider"] is { Length: > 0 } llmProvider ? llmProvider : options.LlmProvider,
            OllamaBaseUrl: section?["OllamaBaseUrl"],
            OllamaEmbeddingModel: section?["OllamaEmbeddingModel"],
            OllamaChatModel: section?["OllamaChatModel"],
            ReindexEnabled: bool.TryParse(section?["ReindexEnabled"], out var reindexEnabled) && reindexEnabled,
            ReindexIntervalMinutes: int.TryParse(section?["ReindexIntervalMinutes"], out var reindexIntervalMinutes) ? reindexIntervalMinutes : 60,
            LastReindexScheduledAt: null,
            SessionSummaryIntervalMinutes: int.TryParse(section?["SessionSummaryIntervalMinutes"], out var sessionSummaryIntervalMinutes)
                ? sessionSummaryIntervalMinutes
                : 0,
            MinEventsForSessionSummary: int.TryParse(section?["MinEventsForSessionSummary"], out var minEventsForSessionSummary)
                ? minEventsForSessionSummary
                : 15,
            MaxMemoryNotesPerSection: int.TryParse(section?["MaxMemoryNotesPerSection"], out var maxMemoryNotesPerSection)
                ? maxMemoryNotesPerSection
                : 20,
            MinEventsForMemoryExtraction: int.TryParse(section?["MinEventsForMemoryExtraction"], out var minEventsForMemoryExtraction)
                ? minEventsForMemoryExtraction
                : 20);
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
