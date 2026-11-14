using Context.Application.Jobs;
using Context.Dashboard.Server.Security;
using Context.Dashboard.Server.Startup;
using Context.Infrastructure.Abstractions;
using Context.Providers;
using Context.Providers.Abstractions;

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
        IStorageMaintenanceService storageMaintenance,
        IJobDispatcherHealth dispatcherHealth,
        IProviderResolver providerResolver,
        CurrentProjectAccessor currentProject,
        IJobRepository jobs,
        HostStartTime startTime,
        CancellationToken ct)
    {
        var databaseReachable = await storageMaintenance.CheckDatabaseAsync(ct).ConfigureAwait(false);

        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(currentProject.ProjectId, ct).ConfigureAwait(false);
        var chatClient = await providerResolver.GetChatClientAsync(currentProject.ProjectId, ct).ConfigureAwait(false);

        var jobDispatcherAlive = dispatcherHealth.IsRunning;

        // This endpoint is served by the same ASP.NET Core process MapMcp() runs in (Program.cs) -
        // if this handler is executing, the MCP transport is necessarily reachable too.
        const bool mcpAlive = true;

        // Scoped to the bearer token's own project (same as JobDispatcher's SignalR
        // "JobCountsChanged" push -- see IJobRepository.CountByStatusAsync) so the Dashboard's stat
        // tiles reflect only the project currently open, not every project's jobs.
        var counts = await jobs.CountByStatusAsync(currentProject.ProjectId, ct).ConfigureAwait(false);

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
