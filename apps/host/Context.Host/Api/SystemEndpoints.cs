using Context.Domain.Enums;
using Context.Host.Security;
using Context.Infrastructure;
using Context.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace Context.Host.Api;

/// <summary>
/// <c>GET /api/system/status</c> — the Dashboard's System/health tab, per CONTRACTS.md §12.
/// Reuses the same checks <see cref="HealthEndpoints"/> exposes at <c>/health</c> rather than
/// computing them a second way.
/// </summary>
public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/api/system/status", StatusAsync).RequireBearerToken();
    }

    private static async Task<IResult> StatusAsync(
        IDbContextFactory<ContextDbContext> dbFactory,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IChatClient chatClient,
        HostStartTime startTime,
        CancellationToken ct)
    {
        var databaseReachable = await HealthChecks.CheckDatabaseAsync(dbFactory, ct).ConfigureAwait(false);

        // JobDispatcher has no finer-grained liveness flag than "the host is up" - see the identical
        // note on HealthEndpoints.HealthAsync's own jobDispatcherAlive.
        const bool jobDispatcherAlive = true;

        // This endpoint is served by the same ASP.NET Core process MapMcp() runs in (Program.cs) -
        // if this handler is executing, the MCP transport is necessarily reachable too.
        const bool mcpAlive = true;

        await using var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var counts = await db.Jobs
            .GroupBy(j => j.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        int CountFor(JobStatus status) => counts.Find(c => c.Key == status)?.Count ?? 0;

        var uptimeSeconds = (DateTimeOffset.UtcNow - startTime.Value).TotalSeconds;

        return Results.Ok(new SystemStatusDto(
            databaseReachable,
            jobDispatcherAlive,
            mcpAlive,
            embeddingGenerator.GetProviderName(),
            chatClient.GetProviderName(),
            CountFor(JobStatus.Pending),
            CountFor(JobStatus.Running),
            CountFor(JobStatus.Failed),
            CountFor(JobStatus.Done),
            uptimeSeconds));
    }
}

public sealed record SystemStatusDto(
    bool DatabaseReachable,
    bool JobDispatcherAlive,
    bool McpAlive,
    string EmbeddingProvider,
    string LlmProvider,
    int JobsPending,
    int JobsRunning,
    int JobsFailed,
    int JobsDone,
    double UptimeSeconds);
