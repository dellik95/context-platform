using Context.Application;
using Context.Dashboard.Server.Security;
using Context.Retrieval.Abstractions;

namespace Context.Dashboard.Server.Api;

/// <summary><c>GET /api/search</c> — thin wrapper over <see cref="IContextEngine.SearchContextAsync"/>.</summary>
public static class SearchEndpoints
{
    private const int DefaultTokenBudget = 2000;

    public static void MapSearchEndpoints(this WebApplication app)
    {
        app.MapGet("/api/search", SearchAsync).RequireBearerToken();
    }

    private static async Task<IResult> SearchAsync(
        string q, int? budget, Guid? sessionId, CurrentProjectAccessor currentProject, IContextEngine engine, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
        {
            return Results.BadRequest("'q' is required.");
        }

        var query = new SearchQuery(currentProject.ProjectId, q, Scope: null, TokenBudget: budget ?? DefaultTokenBudget, SessionId: sessionId);
        var pack = await engine.SearchContextAsync(query, ct).ConfigureAwait(false);
        return Results.Ok(pack);
    }
}
