using Context.Claude.Installation;
using Context.Dashboard.Server.Security;
using Context.Infrastructure.Abstractions;

namespace Context.Dashboard.Server.Api;

/// <summary>
/// <c>/api/projects/{id}/claude/install</c> — thin wrapper over <see cref="IClaudeIntegrationInstaller"/>
/// backing the Dashboard's "install Claude integration" action, per CONTRACTS.md §12.
/// <para/>
/// <c>allowAdmin: false</c> because <see cref="InstallAsync"/> writes the token that authenticated
/// the request into <c>.mcp.json</c> and the hook scripts inside the project's own root: an admin
/// caller has no project token to write there, and writing the host-wide admin secret into a
/// repo on disk instead would be worse than refusing.
/// </summary>
public static class ClaudeEndpoints
{
    public static void MapClaudeEndpoints(this WebApplication app)
    {
        app.MapPost("/api/projects/{id:guid}/claude/install", InstallAsync).RequireOwnProject(allowAdmin: false);
    }

    /// <summary>
    /// Passes the token that already authenticated this request (validated against this exact
    /// project's <see cref="Context.Domain.Entities.Project.BearerTokenHash"/> by
    /// <see cref="ProjectScopeAuth.RequireOwnProjectFilter"/>) straight through to
    /// <see cref="IClaudeIntegrationInstaller.InstallAsync"/>. Without this, a project created via
    /// the Dashboard (no local <c>context-platform.local.json</c> on disk yet) hits
    /// <c>InstallAsync</c>'s fallback and mints a brand-new random token to write into the hook
    /// scripts and <c>.mcp.json</c> — one <see cref="Project.BearerTokenHash"/> never matches, so
    /// every hook call and MCP request the freshly-installed integration makes is rejected.
    /// </summary>
    private static async Task<IResult> InstallAsync(
        Guid id,
        HttpContext http,
        IProjectRepository projects,
        IClaudeIntegrationInstaller installer,
        CancellationToken ct)
    {
        var project = await projects.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (project is null)
        {
            return Results.NotFound();
        }

        var bearerToken = BearerTokenAuth.ExtractToken(http);
        await installer.InstallAsync(id, project.RootPath, ct, bearerToken).ConfigureAwait(false);
        await installer.InstallSkillAsync(id, project.RootPath, ct).ConfigureAwait(false);

        return Results.Ok(new { installed = true });
    }
}
