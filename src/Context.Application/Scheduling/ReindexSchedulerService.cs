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
/// <see cref="IProjectSettingsService.UpdateAsync"/> stamps <c>LastReindexScheduledAt</c> only after
/// the reindex job is queued, so a failed enqueue is retried by the next poll instead of being skipped
/// for a whole interval. The reindex itself is a <see cref="Context.Domain.Enums.JobType.ReindexProject"/>
/// job (same as the Dashboard's manual trigger), so it is cancellable from the Dashboard and one project's
/// failure or slowness can never block another project's turn. A project whose previous run is still
/// queued or running is skipped, never interrupted.
/// </remarks>
public sealed class ReindexSchedulerService(
    IServiceScopeFactory scopeFactory,
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
        var requester = scope.ServiceProvider.GetRequiredService<ReindexRequester>();

        var projects = await projectRepository.ListAsync(ct).ConfigureAwait(false);

        foreach (var project in projects)
        {
            try
            {
                await MaybeScheduleReindexAsync(project.Id, settingsService, requester, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to evaluate reindex schedule for project {ProjectId}.", project.Id);
            }
        }
    }

    private async Task MaybeScheduleReindexAsync(
        Guid projectId, IProjectSettingsService settingsService, ReindexRequester requester, CancellationToken ct)
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

        var now = TimeProvider.GetUtcNow();
        if (await requester.RequestAsync(projectId, full: false, skipIfActive: true, ct).ConfigureAwait(false) is null)
        {
            Logger.LogInformation("Skipped scheduled reindex of project {ProjectId}: one is already queued or running.", projectId);
        }

        await settingsService.UpdateAsync(projectId, settings with { LastReindexScheduledAt = now }, ct).ConfigureAwait(false);
    }
}
