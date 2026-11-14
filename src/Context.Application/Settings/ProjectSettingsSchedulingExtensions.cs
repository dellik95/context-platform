namespace Context.Application.Settings;

internal static class ProjectSettingsSchedulingExtensions
{
    public static Task StampDatabaseScheduleAsync(
        this IProjectSettingsService settingsService,
        Guid projectId,
        Func<ProjectSettingsDto, ProjectSettingsDto> patch,
        CancellationToken ct)
    {
        if (settingsService is ProjectSettingsService concrete)
        {
            return concrete.PatchDatabaseAsync(projectId, patch, ct);
        }

        return StampViaInterfaceAsync(settingsService, projectId, patch, ct);
    }

    private static async Task StampViaInterfaceAsync(
        IProjectSettingsService settingsService,
        Guid projectId,
        Func<ProjectSettingsDto, ProjectSettingsDto> patch,
        CancellationToken ct)
    {
        var latest = await settingsService.GetAsync(projectId, ct).ConfigureAwait(false);
        await settingsService.UpdateAsync(projectId, patch(latest), ct).ConfigureAwait(false);
    }
}
