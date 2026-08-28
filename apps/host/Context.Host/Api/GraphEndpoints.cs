using Context.Application;

namespace Context.Host.Api;

/// <summary><c>GET /api/graph/impact</c> — thin wrapper over <see cref="IContextEngine.GraphImpactAsync"/>.</summary>
public static class GraphEndpoints
{
    private const int DefaultMaxHops = 3;

    public static void MapGraphEndpoints(this WebApplication app)
    {
        app.MapGet("/api/graph/impact", ImpactAsync);
    }

    private static async Task<IResult> ImpactAsync(Guid symbolId, int? maxHops, IContextEngine engine, CancellationToken ct)
    {
        var impact = await engine.GraphImpactAsync(symbolId, maxHops ?? DefaultMaxHops, ct).ConfigureAwait(false);
        return Results.Ok(impact);
    }
}
