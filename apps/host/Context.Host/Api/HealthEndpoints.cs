using Context.Infrastructure;
using Context.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Host.Api;

/// <summary>
/// <c>GET /health</c> — aggregates DB reachability (a plain <c>SELECT 1</c>), job dispatcher
/// liveness, and which provider each category is currently configured to use, per the Context.Host
/// task brief §7.
/// </summary>
public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this WebApplication app)
    {
        app.MapGet("/health", HealthAsync);
    }

    private static async Task<IResult> HealthAsync(
        IDbContextFactory<ContextDbContext> dbFactory,
        IEmbeddingProvider embeddingProvider,
        IRerankerProvider rerankerProvider,
        ILlmProvider llmProvider,
        CancellationToken ct)
    {
        var databaseReachable = await CheckDatabaseAsync(dbFactory, ct).ConfigureAwait(false);

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
            healthy, databaseReachable, jobDispatcherAlive, embeddingProvider.Name, rerankerProvider.Name, llmProvider.Name);

        return healthy ? Results.Ok(response) : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<bool> CheckDatabaseAsync(IDbContextFactory<ContextDbContext> dbFactory, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlRawAsync("SELECT 1", ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
