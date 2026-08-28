using Context.Application;

namespace Context.Host.Api;

/// <summary><c>GET /api/memories</c> — thin wrapper over <see cref="IContextEngine.SearchMemoryAsync"/>.</summary>
public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/memories", SearchAsync);
    }

    private static async Task<IResult> SearchAsync(
        Guid projectId, string? q, bool? includeSuperseded, IContextEngine engine, CancellationToken ct)
    {
        var results = await engine.SearchMemoryAsync(projectId, q ?? string.Empty, includeSuperseded ?? false, ct).ConfigureAwait(false);
        return Results.Ok(results);
    }
}
