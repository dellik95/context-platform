using Context.Domain.Enums;
using Context.Dashboard.Server.Security;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Dashboard.Server.Api;

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
        IProviderResolver providerResolver,
        CurrentProjectAccessor currentProject,
        IJobRepository jobs,
        HostStartTime startTime,
        CancellationToken ct)
    {
        var databaseReachable = await HealthChecks.CheckDatabaseAsync(dbFactory, ct).ConfigureAwait(false);

        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(currentProject.ProjectId, ct).ConfigureAwait(false);
        var chatClient = await providerResolver.GetChatClientAsync(currentProject.ProjectId, ct).ConfigureAwait(false);

        // JobDispatcher has no finer-grained liveness flag than "the host is up" - see the identical
        // note on HealthEndpoints.HealthAsync's own jobDispatcherAlive.
        const bool jobDispatcherAlive = true;

        // This endpoint is served by the same ASP.NET Core process MapMcp() runs in (Program.cs) -
        // if this handler is executing, the MCP transport is necessarily reachable too.
        const bool mcpAlive = true;

        // Shared with JobDispatcher's own SignalR "JobCountsChanged" push (see
        // IJobRepository.CountByStatusAsync) so the REST snapshot and the live push are always
        // computed exactly the same way.
        var counts = await jobs.CountByStatusAsync(ct).ConfigureAwait(false);

        var uptimeSeconds = (DateTimeOffset.UtcNow - startTime.Value).TotalSeconds;

        return Results.Ok(new SystemStatusDto(
            databaseReachable,
            jobDispatcherAlive,
            mcpAlive,
            embeddingGenerator.GetProviderName(),
            chatClient.GetProviderName(),
            counts.Pending,
            counts.Running,
            counts.Failed,
            counts.Done,
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
