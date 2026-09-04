using System.Net;
using Context.Shared;

namespace Context.Dashboard.Server.Security;

/// <summary>
/// Resolves the effective admin token once at startup: <c>CONTEXT_PLATFORM_ADMIN_TOKEN</c> if set,
/// otherwise a freshly generated one — but only when this process is bound loopback-only
/// (<see cref="ContextHostOptions.BindHost"/>). A non-loopback bind (e.g. an operator setting
/// <c>CONTEXT_PLATFORM_BIND_HOST=0.0.0.0</c> for a bare-metal deploy without also setting the
/// token) still fails closed exactly like before — <see cref="Token"/> is <c>null</c>, so
/// <see cref="AdminTokenAuth"/> rejects every request. Docker is unaffected either way:
/// docker-compose.yml already makes the env var mandatory at the container level.
/// <para/>
/// The auto-generated case only ever needs to be known to this same process and whoever calls
/// <c>GET /api/auth/admin-token</c> (also loopback-gated, see AdminTokenBootstrapEndpoints) — there
/// is no cross-restart persistence, so a host restart simply mints a new one and the Dashboard
/// re-fetches it on next load.
/// </summary>
public sealed class AdminTokenProvider
{
    private const string EnvVarName = "CONTEXT_PLATFORM_ADMIN_TOKEN";

    public string? Token { get; }

    public AdminTokenProvider(IConfiguration configuration, ContextHostOptions hostOptions)
    {
        var configured = configuration[EnvVarName];
        Token = !string.IsNullOrEmpty(configured)
            ? configured
            : IsLoopbackOnly(hostOptions.BindHost) ? BearerTokenGenerator.Generate() : null;
    }

    private static bool IsLoopbackOnly(string bindHost) =>
        IPAddress.TryParse(bindHost, out var address) && IPAddress.IsLoopback(address);
}
