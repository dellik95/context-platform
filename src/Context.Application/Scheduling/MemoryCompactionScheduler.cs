using Context.Application.Jobs;
using Context.Application.Settings;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Scheduling;

/// <summary>
/// Polls every project's <see cref="ProjectSettingsDto"/> and enqueues a <see cref="JobType.CompactMemory"/>
/// job once <see cref="ProjectSettingsDto.MemoryCompactionIntervalMinutes"/> has elapsed since
/// <see cref="ProjectSettingsDto.LastMemoryCompactionScheduledAt"/>. Registered via
/// <c>services.AddHostedService&lt;MemoryCompactionScheduler&gt;()</c> in
/// <see cref="ServiceCollectionExtensions.AddJobDispatcher"/>.
/// </summary>
/// <remarks>
/// Unlike <see cref="ReindexSchedulerService"/> (which fires its heavy work itself via
/// <see cref="Task.Run(Func{Task})"/> since reindexing is slow and must not block the poll loop),
/// clustering/merging memories is genuinely slow LLM-bound work that belongs on a
/// <c>JobDispatcher</c> worker, not the poll loop itself - so <see cref="PollOnceAsync"/> only ever
/// does the fast part (a DB write to enqueue <see cref="CompactMemoryPayload"/>), same as every
/// other job producer. <see cref="IProjectSettingsService.UpdateAsync(Guid,ProjectSettingsDto,CancellationToken)"/>
/// stamps <c>LastMemoryCompactionScheduledAt</c> BEFORE enqueueing (not after the job completes) so
/// a slow compaction run can't still be in flight when the next poll ticks and re-enqueues it -
/// same race-avoidance reasoning as <see cref="ReindexSchedulerService"/>'s own
/// <c>LastReindexScheduledAt</c> stamp-before-fire.
/// </remarks>
public sealed class MemoryCompactionScheduler(
    IServiceScopeFactory scopeFactory,
    TimeProvider? timeProvider = null,
    ILogger<MemoryCompactionScheduler>? logger = null)
    : PeriodicScheduler(PollInterval, timeProvider ?? TimeProvider.System, logger ?? NullLogger<MemoryCompactionScheduler>.Instance)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    protected internal override async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var projectRepository = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var settingsService = scope.ServiceProvider.GetRequiredService<IProjectSettingsService>();
        var jobQueue = scope.ServiceProvider.GetRequiredService<IJobQueue>();

        var projects = await projectRepository.ListAsync(ct).ConfigureAwait(false);

        foreach (var project in projects)
        {
            try
            {
                await MaybeScheduleCompactionAsync(project.Id, settingsService, jobQueue, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to evaluate memory-compaction schedule for project {ProjectId}.", project.Id);
            }
        }
    }

    private async Task MaybeScheduleCompactionAsync(Guid projectId, IProjectSettingsService settingsService, IJobQueue jobQueue, CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(projectId, ct).ConfigureAwait(false);
        if (settings.MemoryCompactionIntervalMinutes <= 0)
        {
            return;
        }

        var due = settings.LastMemoryCompactionScheduledAt is null
            || TimeProvider.GetUtcNow() - settings.LastMemoryCompactionScheduledAt.Value >= TimeSpan.FromMinutes(settings.MemoryCompactionIntervalMinutes);
        if (!due)
        {
            return;
        }

        await settingsService.UpdateAsync(projectId, settings with { LastMemoryCompactionScheduledAt = TimeProvider.GetUtcNow() }, ct)
            .ConfigureAwait(false);

        await jobQueue.EnqueueAsync(JobType.CompactMemory, new CompactMemoryPayload(projectId), projectId, ct: ct).ConfigureAwait(false);
    }
}
