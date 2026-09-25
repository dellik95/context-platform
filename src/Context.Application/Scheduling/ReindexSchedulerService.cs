using Context.Application.Indexing;
using Context.Application.Settings;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Scheduling;

/// <summary>
/// Polls every project's <see cref="ProjectSettingsDto"/> and fires a non-full reindex once
/// <see cref="ProjectSettingsDto.ReindexIntervalMinutes"/> has elapsed since
/// <see cref="ProjectSettingsDto.LastReindexScheduledAt"/>, per CONTRACTS.md §12. Registered via
/// <c>services.AddHostedService&lt;ReindexSchedulerService&gt;()</c> in
/// <see cref="ServiceCollectionExtensions.AddContextApplication"/>.
/// </summary>
/// <remarks>
/// <see cref="IProjectSettingsService.UpdateAsync"/> stamps <c>LastReindexScheduledAt</c> BEFORE
/// the reindex is fired (not after it completes) so a slow reindex can't still be running when
/// the next poll ticks and re-triggers it. The reindex itself runs in the background through
/// <see cref="ReindexRunner"/>, same as the Dashboard's manual trigger, so it is cancellable from
/// the Dashboard and one project's failure or slowness can never block another project's turn.
/// A project whose previous run is still going is skipped, never interrupted.
/// </remarks>
public sealed class ReindexSchedulerService(
    IServiceScopeFactory scopeFactory,
    ReindexRunner reindexRunner,
    TimeProvider? timeProvider = null,
    ILogger<ReindexSchedulerService>? logger = null)
    : PeriodicScheduler(PollInterval, timeProvider ?? TimeProvider.System, logger ?? NullLogger<ReindexSchedulerService>.Instance)
{
    // TimeSpan isn't a compile-time constant, so this follows the same static-readonly-in-place-of-
    // const convention JobDispatcher already uses for its own TimeSpan constants.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    protected internal override async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var projectRepository = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var settingsService = scope.ServiceProvider.GetRequiredService<IProjectSettingsService>();

        var projects = await projectRepository.ListAsync(ct).ConfigureAwait(false);

        foreach (var project in projects)
        {
            try
            {
                await MaybeScheduleReindexAsync(project.Id, settingsService, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to evaluate reindex schedule for project {ProjectId}.", project.Id);
            }
        }
    }

    private async Task MaybeScheduleReindexAsync(Guid projectId, IProjectSettingsService settingsService, CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(projectId, ct).ConfigureAwait(false);
        if (!settings.ReindexEnabled || settings.ReindexIntervalMinutes <= 0)
        {
            return;
        }

        var due = settings.LastReindexScheduledAt is null
            || TimeProvider.GetUtcNow() - settings.LastReindexScheduledAt.Value >= TimeSpan.FromMinutes(settings.ReindexIntervalMinutes);
        if (!due)
        {
            return;
        }

        await settingsService.UpdateAsync(projectId, settings with { LastReindexScheduledAt = TimeProvider.GetUtcNow() }, ct)
            .ConfigureAwait(false);

        if (!reindexRunner.TryStart(projectId, full: false))
        {
            Logger.LogInformation("Skipped scheduled reindex of project {ProjectId}: one is already running.", projectId);
        }
    }
}
