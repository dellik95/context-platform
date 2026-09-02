using System.Security.Cryptography;
using System.Text;

namespace Context.Host.Security;

/// <summary>
/// Shared bearer-token authentication for every Context.Host endpoint that isn't
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
/// Compares with <see cref="CryptographicOperations.FixedTimeEquals"/> so response timing can't be
/// used to guess the token byte-by-byte.
/// </summary>
public static class BearerTokenAuth
{
    private const string HeaderPrefix = "Bearer ";
    private const string QueryParamName = "access_token";

    /// <summary>
    /// True when <paramref name="http"/> carries a token that fixed-time-matches
    /// <see cref="HostProjectContext.BearerToken"/>. Always false when no token was ever installed
    /// (fail closed — see <see cref="HostProjectContext"/>'s remarks).
    /// </summary>
    public static bool IsAuthorized(HttpContext http, HostProjectContext project)
    {
        if (string.IsNullOrEmpty(project.BearerToken))
        {
            return false;
        }

        var presentedToken = ExtractToken(http);
        if (presentedToken is null)
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(project.BearerToken);
        var presented = Encoding.UTF8.GetBytes(presentedToken);

        // CryptographicOperations.FixedTimeEquals requires equal-length spans; a length mismatch
        // is itself decided in the same fixed time as any other same-length comparison would take
        // (this is the standard shape for a fixed-time bearer-token check with a variable-length
        // secret - see e.g. ASP.NET Core's own API key auth samples).
        return expected.Length == presented.Length && CryptographicOperations.FixedTimeEquals(expected, presented);
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

    /// <summary>Endpoint filter form, for <c>.AddEndpointFilter(BearerTokenAuth.RequireBearerTokenFilter)</c>.</summary>
    public static async ValueTask<object?> RequireBearerTokenFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var project = ctx.HttpContext.RequestServices.GetRequiredService<HostProjectContext>();
        if (!IsAuthorized(ctx.HttpContext, project))
        {
            return Results.Unauthorized();
        }

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
