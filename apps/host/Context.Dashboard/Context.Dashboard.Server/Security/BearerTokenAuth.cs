using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Context.Shared;

namespace Context.Dashboard.Server.Security;

/// <summary>
/// Shared bearer-token authentication for every Context.Dashboard.Server endpoint that isn't
/// <c>/health</c> or the static-file/SPA shell. Originated in <c>Hooks.HookEndpoints</c> (hooks
/// were the first thing that needed it); extracted here so REST endpoints, <c>/mcp</c>, and the
/// SignalR hub middleware all share exactly one check instead of three copies drifting apart.
/// <para/>
/// Accepts the token from either the standard <c>Authorization: Bearer</c> header, or an
/// <c>access_token</c> query-string parameter. The query-string fallback exists solely for the
/// SignalR hub: browsers cannot set custom headers on a WebSocket upgrade, so the JS client's
/// <c>accessTokenFactory</c> option sends the token as <c>?access_token=...</c> instead (this is
/// SignalR's own documented mechanism for exactly this limitation). REST/MCP callers are expected
/// to keep using the header — the query string is accepted everywhere only because there is no
/// endpoint-level way to say "hub paths only", and a token in a query string is no more exposed
/// here than it already is in the hub's own negotiate URL.
/// <para/>
/// Phase 3: resolves the presented token to whichever <see cref="Project"/> it belongs to, rather
/// than fixed-time-comparing it against one process-wide token — this is what lets a single
/// Context.Dashboard.Server process eventually serve more than one project. The token itself is hashed with
/// <see cref="Hashing.HashToken"/> (SHA-256) and looked up by hash via
/// <see cref="IProjectRepository.GetByBearerTokenHashAsync"/>; a fixed-time compare isn't needed
/// here the way it is for <see cref="AdminTokenAuth"/>'s single shared secret — a hash-indexed DB
/// lookup isn't a byte-by-byte comparison an attacker can time against.
/// </summary>
public static class BearerTokenAuth
{
    private const string HeaderPrefix = "Bearer ";
    private const string QueryParamName = "access_token";

    /// <summary>
    /// Resolves the <see cref="Project"/> whose <see cref="Project.BearerTokenHash"/> matches the
    /// token carried on <paramref name="http"/>, or <c>null</c> when no token was presented or it
    /// doesn't match any project (fail closed).
    /// </summary>
    public static Task<Project?> TryResolveProjectAsync(HttpContext http, IProjectRepository projects, CancellationToken ct)
    {
        var presentedToken = ExtractToken(http);
        if (presentedToken is null)
        {
            return Task.FromResult<Project?>(null);
        }

        var tokenHash = Hashing.HashToken(presentedToken);
        return projects.GetByBearerTokenHashAsync(tokenHash, ct);
    }

    private static string? ExtractToken(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        if (header.StartsWith(HeaderPrefix, StringComparison.Ordinal))
        {
            return header[HeaderPrefix.Length..];
        }

        if (http.Request.Query.TryGetValue(QueryParamName, out var queryToken) && queryToken.Count > 0)
        {
            return queryToken[0];
        }

        return null;
    }

    /// <summary>
    /// Endpoint filter form, for <c>.AddEndpointFilter(BearerTokenAuth.RequireBearerTokenFilter)</c>.
    /// On success, stashes the resolved <see cref="Project"/> into <c>HttpContext.Items</c> under
    /// <see cref="CurrentProjectAccessor.HttpContextItemKey"/> so <see cref="CurrentProjectAccessor"/>
    /// can read it back later in the same request.
    /// </summary>
    public static async ValueTask<object?> RequireBearerTokenFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var projects = ctx.HttpContext.RequestServices.GetRequiredService<IProjectRepository>();
        var project = await TryResolveProjectAsync(ctx.HttpContext, projects, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
        if (project is null)
        {
            return Results.Unauthorized();
        }

        ctx.HttpContext.Items[CurrentProjectAccessor.HttpContextItemKey] = project;

        return await next(ctx).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>.RequireBearerToken()</c> — chainable sugar over <c>.AddEndpointFilter(BearerTokenAuth.RequireBearerTokenFilter)</c>,
/// generic over <see cref="IEndpointConventionBuilder"/> so it works on a single mapped route
/// (<c>RouteHandlerBuilder</c>), a <c>MapGroup</c> (<c>RouteGroupBuilder</c>), and <c>MapMcp</c>'s
/// return value (also a <c>RouteGroupBuilder</c> — confirmed by inspecting the real
/// <c>ModelContextProtocol.AspNetCore</c> package's runtime type).
/// </summary>
public static class BearerTokenAuthEndpointExtensions
{
    public static TBuilder RequireBearerToken<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(BearerTokenAuth.RequireBearerTokenFilter);
}
