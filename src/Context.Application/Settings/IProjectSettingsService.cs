namespace Context.Application.Settings;

public interface IProjectSettingsService
{
    // Returns effective settings merged from appsettings defaults, Project.ConfigJson (database),
    // and optional project-local overrides in .claude/context-platform.local.json (settings node).
    Task<ProjectSettingsDto> GetAsync(Guid projectId, CancellationToken ct);

    // Serializes settings to Project.ConfigJson and persists via IProjectRepository.UpdateAsync.
    // Used by internal services (schedulers) for DB-backed state updates.
    Task UpdateAsync(Guid projectId, ProjectSettingsDto settings, CancellationToken ct);

    // Writes settings into the project-local .claude/context-platform.local.json "settings" node.
    Task UpdateLocalAsync(Guid projectId, ProjectSettingsDto settings, CancellationToken ct);

    // Applies the requested write target for runtime API callers.
    Task UpdateAsync(Guid projectId, ProjectSettingsDto settings, ProjectSettingsWriteTarget target, CancellationToken ct);
}
