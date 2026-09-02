using Context.Claude.Installation;
using Context.Host.Security;
using Context.Infrastructure.Abstractions;

namespace Context.Host.Api;

/// <summary>
/// <c>/api/projects/{id}/claude/install</c> — thin wrapper over <see cref="IClaudeIntegrationInstaller"/>
/// backing the Dashboard's "install Claude integration" action, per CONTRACTS.md §12.
/// </summary>
public static class ClaudeEndpoints
{
    public static void MapClaudeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/projects/{id:guid}/claude/install", InstallAsync).RequireBearerToken();
    }

    private static async Task<IResult> InstallAsync(
        Guid id,
        IProjectRepository projects,
        IClaudeIntegrationInstaller installer,
        CancellationToken ct)
    {
        var project = await projects.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (project is null)
        {
            return Results.NotFound();
        }

        await installer.InstallAsync(id, project.RootPath, ct).ConfigureAwait(false);
        await installer.InstallSkillAsync(id, project.RootPath, ct).ConfigureAwait(false);

        return Results.Ok(new { installed = true });
    }
}
