using Context.Application.Jobs;
using Context.Application.Settings;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Scheduling;

/// <summary>
/// Enqueues an applying <see cref="JobType.ReconsolidateMemory"/> run for each project once
/// <see cref="ProjectSettingsDto.MemoryReconsolidationIntervalMinutes"/> has elapsed since
/// <see cref="ProjectSettingsDto.LastMemoryReconsolidationScheduledAt"/>, but only when live knowledge
/// was learned or reinforced since that stamp; an unchanged project would only re-plan the same groups.
/// </summary>
/// <remarks>
/// The stamp is written before enqueueing, and on every due evaluation, so a slow run cannot be
/// enqueued twice and an idle project is re-checked once per interval rather than every poll.
/// </remarks>
public sealed class MemoryReconsolidationScheduler(
    IServiceScopeFactory scopeFactory,
    TimeProvider? timeProvider = null,
    ILogger<MemoryReconsolidationScheduler>? logger = null)
    : PeriodicScheduler(PollInterval, timeProvider ?? TimeProvider.System, logger ?? NullLogger<MemoryReconsolidationScheduler>.Instance)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    protected internal override async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var projects = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var settingsService = scope.ServiceProvider.GetRequiredService<IProjectSettingsService>();
        var memories = scope.ServiceProvider.GetRequiredService<IMemoryRepository>();
        var jobQueue = scope.ServiceProvider.GetRequiredService<IJobQueue>();

        foreach (var project in await projects.ListAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await MaybeScheduleAsync(project.Id, settingsService, memories, jobQueue, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "Failed to evaluate the memory reconsolidation schedule for project {ProjectId}.", project.Id);
            }
        }
    }

    private async Task MaybeScheduleAsync(
        Guid projectId, IProjectSettingsService settingsService, IMemoryRepository memories, IJobQueue jobQueue, CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(projectId, ct).ConfigureAwait(false);
        if (settings.MemoryReconsolidationIntervalMinutes <= 0)
        {
            return;
        }

        var now = TimeProvider.GetUtcNow();
        var last = settings.LastMemoryReconsolidationScheduledAt;
        if (last is not null && now - last.Value < TimeSpan.FromMinutes(settings.MemoryReconsolidationIntervalMinutes))
        {
            return;
        }

        var live = await memories.ListByProjectAsync(projectId, includeSuperseded: false, ct).ConfigureAwait(false);
        if (live.Any(m => last is null || m.LastReinforcedAt > last.Value))
        {
            await jobQueue.EnqueueAsync(
                JobType.ReconsolidateMemory, new ReconsolidateMemoryPayload(projectId, DryRun: false), projectId, JobPriority.Session, ct).ConfigureAwait(false);
        }

        // Stamped only once the job is queued: a failed enqueue is then retried by the next poll instead of
        // being skipped for a whole interval.
        await settingsService.UpdateAsync(projectId, settings with { LastMemoryReconsolidationScheduledAt = now }, ct).ConfigureAwait(false);
    }
}
