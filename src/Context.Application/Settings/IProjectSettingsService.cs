namespace Context.Application.Settings;

public interface IProjectSettingsService
{
    // Returns saved settings from Project.ConfigJson; if a project has never saved settings,
    // returns a DTO seeded from the host's current IOptions<Context.Providers.ContextProvidersOptions>
    // (so the UI shows real effective values, not blanks) with ReindexEnabled=false, ReindexIntervalMinutes=60.
    Task<ProjectSettingsDto> GetAsync(Guid projectId, CancellationToken ct);

    // Serializes settings to Project.ConfigJson and persists via IProjectRepository.UpdateAsync.
    Task UpdateAsync(Guid projectId, ProjectSettingsDto settings, CancellationToken ct);
}
