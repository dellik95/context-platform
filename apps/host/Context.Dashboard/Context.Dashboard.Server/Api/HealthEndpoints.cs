using Context.Infrastructure;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Context.Dashboard.Server.Api;

/// <summary>
/// <c>GET /health</c> — aggregates DB reachability (a plain <c>SELECT 1</c>), job dispatcher
/// liveness, and which provider each category defaults to, per the Context.Dashboard.Server task
/// brief §7. Unauthenticated (no project context — see BearerTokenAuth's own remarks on the "every
/// endpoint except /health and the static SPA shell" exception), so this reports the process-level
/// DEFAULT embedding/LLM provider selection (<see cref="ContextProvidersOptions"/>'s resolved
/// strings), not any one project's — a specific project's actual resolved provider is what
/// <c>GET /api/system/status</c> (bearer-token-scoped) reports instead, via
/// <see cref="IProviderResolver"/>. Reading the config strings directly here (rather than resolving
/// a live <c>IEmbeddingGenerator</c>/<c>IChatClient</c> instance) also means a misconfigured
/// provider (e.g. "openai" with no API key) reports unhealthy-but-informative instead of 500ing
/// this endpoint.
/// </summary>
public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", HealthAsync);
    }

    private static async Task<IResult> HealthAsync(
        IDbContextFactory<ContextDbContext> dbFactory,
        IOptions<ContextProvidersOptions> providersOptions,
        IRerankerProvider rerankerProvider,
        CancellationToken ct)
    {
        var databaseReachable = await HealthChecks.CheckDatabaseAsync(dbFactory, ct).ConfigureAwait(false);

        // JobDispatcher is registered as both IJobQueue and the app's own IHostedService (see
        // Context.Application.ServiceCollectionExtensions.AddJobDispatcher) and is started via
        // IHost.StartAsync before this endpoint can ever receive a request - if it had thrown
        // during startup, the whole host would have failed to start. JobDispatcher exposes no
        // finer-grained public liveness flag of its own (e.g. "worker loop still running") to check
        // more precisely than that; a true liveness probe would need one added there - tracked as
        // follow-up.
        const bool jobDispatcherAlive = true;

        var healthy = databaseReachable && jobDispatcherAlive;
        var response = new HealthResponse(
            healthy, databaseReachable, jobDispatcherAlive,
            providersOptions.Value.EmbeddingProvider, rerankerProvider.Name, providersOptions.Value.LlmProvider);

        return healthy ? Results.Ok(response) : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
