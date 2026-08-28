using Context.Application;
using Context.Retrieval.Abstractions;

namespace Context.Host.Api;

/// <summary><c>GET /api/search</c> — thin wrapper over <see cref="IContextEngine.SearchContextAsync"/>.</summary>
public static class SearchEndpoints
{
    private const int DefaultTokenBudget = 2000;

    public static void MapSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/search", SearchAsync);
    }

    private static async Task<IResult> SearchAsync(
        Guid projectId, string q, int? budget, Guid? sessionId, IContextEngine engine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.BadRequest("'q' is required.");
        }

        var query = new SearchQuery(projectId, q, Scope: null, TokenBudget: budget ?? DefaultTokenBudget, SessionId: sessionId);
        var pack = await engine.SearchContextAsync(query, ct).ConfigureAwait(false);
        return Results.Ok(pack);
    }
}
