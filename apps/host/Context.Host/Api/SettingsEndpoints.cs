using Context.Application.Settings;

namespace Context.Host.Api;

/// <summary>
/// <c>/api/projects/{id}/settings</c> — thin wrapper over <see cref="IProjectSettingsService"/>
/// backing the Dashboard's Settings tab, per CONTRACTS.md §12.
/// </summary>
public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{id:guid}/settings");

        group.MapGet("/", GetAsync);
        group.MapPut("/", UpdateAsync);
    }

    private static async Task<IResult> GetAsync(Guid id, IProjectSettingsService settings, CancellationToken ct)
    {
        try
        {
            return Results.Ok(await settings.GetAsync(id, ct).ConfigureAwait(false));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }

    private static async Task<IResult> UpdateAsync(
        Guid id,
        ProjectSettingsDto dto,
        IProjectSettingsService settings,
        ProjectSettingsWriteTarget? target,
        CancellationToken ct)
    {
        try
        {
            await settings.UpdateAsync(id, dto, target ?? ProjectSettingsWriteTarget.Both, ct).ConfigureAwait(false);
            return Results.NoContent();
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
    }
}
