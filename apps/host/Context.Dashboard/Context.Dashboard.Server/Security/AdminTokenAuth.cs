using System.Security.Cryptography;
using System.Text;

namespace Context.Dashboard.Server.Security;

/// <summary>
/// Separate, simpler fixed-time-compare auth check — the same shape <see cref="BearerTokenAuth"/>
/// used before Phase 3 moved it to a per-project DB lookup — gated on the single shared token
/// <see cref="AdminTokenProvider"/> resolves once at startup (either
/// <c>CONTEXT_PLATFORM_ADMIN_TOKEN</c>, or an auto-generated one for a loopback-only bind — see its
/// own remarks).
/// <para/>
/// Exists for endpoints that create or list projects (wired up in a later stage): a brand-new
/// project doesn't have a per-project bearer token yet, so it can't authenticate via
/// <see cref="BearerTokenAuth"/> — something has to gate the endpoint that mints the first one.
/// <para/>
/// Fails closed exactly like the old single-token <see cref="BearerTokenAuth"/> did:
/// <see cref="AdminTokenProvider.Token"/> is only ever <c>null</c> when this deployment neither
/// configured a token nor is loopback-only — every request is rejected in that case, there is no
/// "admin auth disabled" mode. Header-only (no <c>access_token</c> query-string fallback): unlike
/// <see cref="BearerTokenAuth"/>, nothing gated by this ever runs over a WebSocket/SignalR
/// upgrade, so there's no browser limitation to work around.
/// </summary>
public static class AdminTokenAuth
{
    private const string HeaderPrefix = "Bearer ";

    /// <summary>
    /// True when <paramref name="http"/> carries a token that fixed-time-matches
    /// <paramref name="adminToken"/>'s resolved <see cref="AdminTokenProvider.Token"/>. Always
    /// false when that's <c>null</c> (fail closed).
    /// </summary>
    public static bool IsAuthorized(HttpContext http, AdminTokenProvider adminToken)
    {
        if (string.IsNullOrEmpty(adminToken.Token))
        {
            return false;
        }

        var presentedToken = ExtractToken(http);
        if (presentedToken is null)
        {
            return false;
        }

        var expected = Encoding.UTF8.GetBytes(adminToken.Token);
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
        return header.StartsWith(HeaderPrefix, StringComparison.Ordinal) ? header[HeaderPrefix.Length..] : null;
    }

    /// <summary>Endpoint filter form, for <c>.AddEndpointFilter(AdminTokenAuth.RequireAdminTokenFilter)</c>.</summary>
    public static async ValueTask<object?> RequireAdminTokenFilter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var adminToken = ctx.HttpContext.RequestServices.GetRequiredService<AdminTokenProvider>();
        if (!IsAuthorized(ctx.HttpContext, adminToken))
        {
            return Results.Unauthorized();
        }

        return await next(ctx).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>.RequireAdminToken()</c> — chainable sugar over <c>.AddEndpointFilter(AdminTokenAuth.RequireAdminTokenFilter)</c>,
/// mirroring <see cref="BearerTokenAuthEndpointExtensions.RequireBearerToken{TBuilder}"/>.
/// </summary>
public static class AdminTokenAuthEndpointExtensions
{
    public static TBuilder RequireAdminToken<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(AdminTokenAuth.RequireAdminTokenFilter);
}
