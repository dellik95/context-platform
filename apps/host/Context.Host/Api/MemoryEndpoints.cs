using Context.Application;
using Context.Host.Security;
using Context.Infrastructure.Abstractions;

namespace Context.Host.Api;

/// <summary><c>GET /api/memories</c> — thin wrapper over <see cref="IContextEngine.SearchMemoryAsync"/>,
/// plus <c>DELETE /api/memories/{id}</c> for a user explicitly discarding one.</summary>
public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("").RequireBearerToken();
        group.MapGet("/api/memories", SearchAsync);
        group.MapDelete("/api/memories/{id:guid}", DeleteAsync);
    }

    private static async Task<IResult> SearchAsync(
        Guid projectId, string? q, bool? includeSuperseded, IContextEngine engine, CancellationToken ct)
    {
        var results = await engine.SearchMemoryAsync(projectId, q ?? string.Empty, includeSuperseded ?? false, ct).ConfigureAwait(false);
        return Results.Ok(results);
    }

    private static async Task<IResult> DeleteAsync(Guid id, IMemoryRepository memories, CancellationToken ct)
    {
        await memories.DeleteAsync(id, ct).ConfigureAwait(false);
        return Results.NoContent();
    }
}
