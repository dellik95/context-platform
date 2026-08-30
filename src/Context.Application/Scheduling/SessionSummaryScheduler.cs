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
/// Polls every project's <see cref="ProjectSettingsDto.SessionSummaryIntervalMinutes"/> and, for
/// each still-Active session past that interval since its last summary (or since it started, if
/// it has none yet), enqueues a mid-session <c>SummarizeSession</c> job - the same job
/// <c>EndSessionAsync</c> fires, just triggered on a timer instead of session end. This is what
/// lets the Dashboard's Sessions/Memories panels show something for a session that's still
/// running, rather than only ever seeing a summary once it ends.
/// </summary>
/// <remarks>
/// Same fire-and-forget-per-project shape as <see cref="ReindexSchedulerService"/>: one project
/// (or one session within it) failing must never block another's turn or delay the next poll.
/// </remarks>
public sealed class SessionSummaryScheduler : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private const int MaxSessionsPerProjectPerPoll = 100;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SessionSummaryScheduler> _logger;

    public SessionSummaryScheduler(IServiceScopeFactory scopeFactory, ILogger<SessionSummaryScheduler>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger ?? NullLogger<SessionSummaryScheduler>.Instance;
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
        var sessionRepository = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var jobQueue = scope.ServiceProvider.GetRequiredService<IJobQueue>();

        var projects = await projectRepository.ListAsync(ct).ConfigureAwait(false);

        foreach (var project in projects)
        {
            try
            {
                await MaybeScheduleSummariesAsync(project.Id, settingsService, sessionRepository, jobQueue, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to evaluate session-summary schedule for project {ProjectId}.", project.Id);
            }
        }
    }

    private async Task MaybeScheduleSummariesAsync(
        Guid projectId,
        IProjectSettingsService settingsService,
        ISessionRepository sessionRepository,
        IJobQueue jobQueue,
        CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(projectId, ct).ConfigureAwait(false);
        if (settings.SessionSummaryIntervalMinutes <= 0)
        {
            return;
        }

        var interval = TimeSpan.FromMinutes(settings.SessionSummaryIntervalMinutes);
        var sessions = await sessionRepository.ListByProjectAsync(projectId, MaxSessionsPerProjectPerPoll, ct).ConfigureAwait(false);

        foreach (var session in sessions)
        {
            if (session.Status != SessionStatus.Active)
            {
                continue;
            }

            try
            {
                var summary = await sessionRepository.GetSummaryAsync(session.Id, ct).ConfigureAwait(false);
                var lastSummarizedAt = summary?.GeneratedAt ?? session.StartedAt;
                if (DateTimeOffset.UtcNow - lastSummarizedAt < interval)
                {
                    continue;
                }

                await jobQueue.EnqueueAsync(JobType.SummarizeSession, new SummarizeSessionPayload(session.Id), ct: ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to schedule a periodic summary for session {SessionId}.", session.Id);
            }
        }
    }
}
