using Context.Application;
using Context.Dashboard.Server.Security;
using Context.Infrastructure.Abstractions;

namespace Context.Dashboard.Server.Api;

/// <summary><c>GET /api/memories</c> — thin wrapper over <see cref="IContextEngine.SearchMemoryAsync"/>,
/// plus <c>DELETE /api/memories/{id}</c> for a user explicitly discarding one. Both take an id the
/// caller supplies, so both are scoped to the project the caller's bearer token resolved to — the
/// query parameter by <see cref="ProjectScopeAuth.RequireMatchingProjectIdFilter"/>, the memory id
/// by the ownership check in <see cref="DeleteAsync"/>.</summary>
public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("").RequireBearerToken().RequireMatchingProjectId();
        group.MapGet("/api/memories", SearchAsync);
        group.MapDelete("/api/memories/{id:guid}", DeleteAsync);
    }

    private static async Task<IResult> SearchAsync(
        Guid? projectId, string? q, bool? includeSuperseded,
        IContextEngine engine, CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        var results = await engine
            .SearchMemoryAsync(projectId ?? currentProject.ProjectId, q ?? string.Empty, includeSuperseded ?? false, ct)
            .ConfigureAwait(false);
        return Results.Ok(results);
    }

    private static async Task<IResult> DeleteAsync(
        Guid id, IMemoryRepository memories, CurrentProjectAccessor currentProject, CancellationToken ct)
    {
        // A memory belonging to another project reports 404 rather than 403: the caller has no
        // business learning whether that id exists at all.
        var memory = await memories.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (memory is null || memory.ProjectId != currentProject.ProjectId)
        {
            return Results.NotFound();
        }

        await memories.DeleteAsync(id, ct).ConfigureAwait(false);
        return Results.NoContent();
    }
}
