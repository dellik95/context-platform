using System.Net;
using Context.Dashboard.Server.Security;

namespace Context.Dashboard.Server.Api;

/// <summary>
/// <c>GET /api/auth/admin-token</c> — the one deliberately unauthenticated endpoint besides
/// <c>/health</c> and the static SPA shell: lets the Dashboard, when it's running on the same
/// machine as this host, learn the admin token (see <see cref="AdminTokenProvider"/>) without a
/// login screen, so <c>POST/GET /api/projects</c> and <c>GET /api/fs/browse</c> just work.
/// <para/>
/// Safe specifically because it's loopback-gated here too, independent of
/// <see cref="AdminTokenProvider"/>'s own loopback-only auto-generate condition: even a host bound
/// to <c>0.0.0.0</c> (container/exposed) only answers this to a caller whose own
/// <see cref="HttpContext.Connection"/> resolves as loopback — nothing on another machine, across a
/// Docker bridge network or otherwise, can reach this response.
/// </summary>
public static class AdminTokenBootstrapEndpoints
{
    public static void MapAdminTokenBootstrapEndpoints(this WebApplication app)
    {
        app.MapGet("/api/auth/admin-token", GetAdminToken);
    }

    private static IResult GetAdminToken(HttpContext http, AdminTokenProvider adminToken)
    {
        var remoteIp = http.Connection.RemoteIpAddress;
        if (remoteIp is null || !IPAddress.IsLoopback(remoteIp) || string.IsNullOrEmpty(adminToken.Token))
        {
            return Results.Problem("Admin token bootstrap is only available to a loopback caller.", statusCode: StatusCodes.Status403Forbidden);
        }

        return Results.Ok(new AdminTokenResponse(adminToken.Token));
    }
}

public sealed record AdminTokenResponse(string Token);
