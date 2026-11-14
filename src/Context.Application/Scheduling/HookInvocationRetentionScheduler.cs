using Context.Application.Settings;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Scheduling;

/// <summary>
/// Polls every project's <see cref="ProjectSettingsDto.HookInvocationRetentionDays"/> and deletes
/// any <c>HookInvocation</c> rows older than that window. Unlike <c>SessionEvents</c> (folded into
/// a summary chunk and cleaned up right after, see <c>SummarizeSessionJobHandler</c>), hook
/// invocations are never consumed by anything downstream - they exist purely for the dashboard's
/// live activity feed - so nothing else in the pipeline ever deletes them. Without this scheduler
/// the table grows forever.
/// </summary>
/// <remarks>
/// Same fire-and-forget-per-project shape as <see cref="StaleSessionScheduler"/>: one project
/// failing must never block another's turn or delay the next poll. Runs far less often than that
/// scheduler's 60s - a retention sweep has no latency requirement, so an hourly poll is plenty.
/// </remarks>
public sealed class HookInvocationRetentionScheduler(
    IServiceScopeFactory scopeFactory,
    TimeProvider? timeProvider = null,
    ILogger<HookInvocationRetentionScheduler>? logger = null)
    : PeriodicScheduler(PollInterval, timeProvider ?? TimeProvider.System, logger ?? NullLogger<HookInvocationRetentionScheduler>.Instance)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    protected internal override async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var projectRepository = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var settingsService = scope.ServiceProvider.GetRequiredService<IProjectSettingsService>();
        var hookInvocationRepository = scope.ServiceProvider.GetRequiredService<IHookInvocationRepository>();

        var projects = await projectRepository.ListAsync(ct).ConfigureAwait(false);

        foreach (var project in projects)
        {
            try
            {
                await MaybeCleanUpAsync(project.Id, settingsService, hookInvocationRepository, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "Failed to evaluate hook-invocation retention for project {ProjectId}.", project.Id);
            }
        }
    }

    private async Task MaybeCleanUpAsync(
        Guid projectId,
        IProjectSettingsService settingsService,
        IHookInvocationRepository hookInvocationRepository,
        CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(projectId, ct).ConfigureAwait(false);
        if (settings.HookInvocationRetentionDays <= 0)
        {
            return;
        }

        var cutoff = TimeProvider.GetUtcNow() - TimeSpan.FromDays(settings.HookInvocationRetentionDays);
        await hookInvocationRepository.DeleteOlderThanAsync(projectId, cutoff, ct).ConfigureAwait(false);
    }
}
