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

    // SummarizeSession calls a local LLM and can easily take longer than one poll interval,
    // especially with several sessions' jobs queued up behind each other on one Ollama instance.
    // GetSummaryAsync's GeneratedAt only updates once a job actually finishes, so without this the
    // due-check re-fires the SAME session every single poll while its previous job is still
    // in-flight — confirmed live: a 1-minute interval against 10 never-summarized sessions queued
    // dozens of duplicate jobs within a few minutes. This in-memory, per-instance guard (reset on
    // restart; that's fine, worst case is one extra summary) tracks "already asked for" separately
    // from "confirmed generated" so a slow job blocks its own re-enqueue instead of stacking more.
    private readonly Dictionary<Guid, DateTimeOffset> _lastEnqueuedAt = new();

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

        foreach (var item in sessions)
        {
            var session = item.Session;
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

                if (_lastEnqueuedAt.TryGetValue(session.Id, out var lastEnqueued)
                    && DateTimeOffset.UtcNow - lastEnqueued < interval)
                {
                    continue;
                }

                // The interval is a MAXIMUM wait, not a guarantee: firing on elapsed time alone
                // would summarize an idle or barely-active session just because the clock ran out,
                // burning an LLM call on (and creating a low-value memory from) almost nothing -
                // codemem batches raw events the same way before its own observer runs, for the
                // same reason. A quiet session just waits longer instead of generating noise.
                var events = await sessionRepository.GetEventsAsync(session.Id, ct).ConfigureAwait(false);
                var newEventCount = events.Count(e => e.Timestamp > lastSummarizedAt);
                if (newEventCount < settings.MinEventsForSessionSummary)
                {
                    continue;
                }

                _lastEnqueuedAt[session.Id] = DateTimeOffset.UtcNow;
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
