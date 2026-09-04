using Context.Application.Abstractions;
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
/// the next poll ticks and re-triggers it. The reindex itself runs via a fire-and-forget
/// <see cref="Task.Run(Func{Task})"/> against its own fresh <see cref="IServiceScope"/> — same
/// shape as <c>Context.Dashboard.Server.Api.ProjectEndpoints.TriggerIndex</c> — so one project's failure or
/// slowness can never block another project's turn or delay the next poll.
/// </remarks>
public sealed class ReindexSchedulerService : BackgroundService
{
    // TimeSpan isn't a compile-time constant, so this follows the same static-readonly-in-place-of-
    // const convention JobDispatcher already uses for its own TimeSpan constants.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ReindexSchedulerService> _logger;

    public ReindexSchedulerService(IServiceScopeFactory scopeFactory, ILogger<ReindexSchedulerService>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger ?? NullLogger<ReindexSchedulerService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnceAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
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
                _logger.LogError(ex, "Failed to evaluate reindex schedule for project {ProjectId}.", project.Id);
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
            || DateTimeOffset.UtcNow - settings.LastReindexScheduledAt.Value >= TimeSpan.FromMinutes(settings.ReindexIntervalMinutes);
        if (!due)
        {
            return;
        }

        await settingsService.UpdateAsync(projectId, settings with { LastReindexScheduledAt = DateTimeOffset.UtcNow }, ct)
            .ConfigureAwait(false);

        FireReindex(projectId);
    }

    private void FireReindex(Guid projectId)
    {
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<IIndexingPipeline>();
            try
            {
                await pipeline.ReindexProjectAsync(projectId, full: false, CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("Scheduled reindex of project {ProjectId} completed.", projectId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Scheduled reindex of project {ProjectId} failed.", projectId);
            }
        });
    }
}
