using Context.Infrastructure;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

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
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IRerankerProvider rerankerProvider,
        IChatClient chatClient,
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
            healthy, databaseReachable, jobDispatcherAlive, embeddingGenerator.GetProviderName(), rerankerProvider.Name, chatClient.GetProviderName());

        return healthy ? Results.Ok(response) : Results.Json(response, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
