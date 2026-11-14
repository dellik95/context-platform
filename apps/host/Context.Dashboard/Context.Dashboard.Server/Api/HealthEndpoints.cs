using Context.Application.Jobs;
using Context.Infrastructure.Abstractions;
using Context.Providers;
using Context.Providers.Abstractions;
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
        IStorageMaintenanceService storageMaintenance,
        IJobDispatcherHealth dispatcherHealth,
        IOptions<ContextProvidersOptions> providersOptions,
        IRerankerProvider rerankerProvider,
        CancellationToken ct)
    {
        var databaseReachable = await storageMaintenance.CheckDatabaseAsync(ct).ConfigureAwait(false);
        var jobDispatcherAlive = dispatcherHealth.IsRunning;

        var healthy = databaseReachable && jobDispatcherAlive;
        var response = new HealthResponse(
            healthy, databaseReachable, jobDispatcherAlive,
            providersOptions.Value.EmbeddingProvider, rerankerProvider.Name, providersOptions.Value.LlmProvider);

        return healthy ? Results.Ok(response) : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
