using Context.Application;
using Context.Dashboard.Server.Security;

namespace Context.Dashboard.Server.Api;

/// <summary><c>GET /api/memories</c> (search), <c>GET /api/memories/{id}</c> (memory with evidence and
/// entities) and <c>DELETE /api/memories/{id}</c>. Scoped to the project the caller's bearer token
/// resolved to: the query parameter by <see cref="ProjectScopeAuth.RequireMatchingProjectIdFilter"/>,
/// a memory id by the engine's ownership check (another project's memory reports 404, not 403).</summary>
public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("").RequireBearerToken().RequireMatchingProjectId();
        group.MapGet("/api/memories", SearchAsync);
        group.MapGet("/api/memories/{id:guid}", GetAsync);
        group.MapDelete("/api/memories/{id:guid}", DeleteAsync);
    }

    private static async Task<IResult> SearchAsync(
        Guid? projectId, string? q, bool? includeSuperseded, string? topic,
        IContextEngine engine, CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        var results = await engine
            .SearchMemoryAsync(projectId ?? currentProject.ProjectId, q ?? string.Empty, includeSuperseded ?? false, ct, topic)
            .ConfigureAwait(false);
        return Results.Ok(results);
    }

    private static async Task<IResult> GetAsync(Guid id, IContextEngine engine, CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        var detail = await engine.GetMemoryAsync(currentProject.ProjectId, id, ct).ConfigureAwait(false);
        return detail is null ? Results.NotFound() : Results.Ok(detail);
    }

    private static async Task<IResult> DeleteAsync(Guid id, IContextEngine engine, CurrentProjectAccessor currentProject, CancellationToken ct) =>
        await engine.ForgetMemoryAsync(currentProject.ProjectId, id, ct).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound();
}
