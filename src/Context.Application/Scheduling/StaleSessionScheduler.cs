using Context.Application.Settings;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Scheduling;

/// <summary>
/// Polls every project's <see cref="ProjectSettingsDto.StaleSessionTimeoutMinutes"/> and ends any
/// still-Active session whose last activity is older than that timeout - a killed terminal or
/// crashed CLI process otherwise leaves its Session row Active forever, since nothing but the
/// SessionEnd hook itself ever ends one. Ends it through <see cref="IContextEngine.EndSessionAsync"/>,
/// the exact same path a real SessionEnd hook uses (reason "stale-timeout"), so it gets a final
/// summary + memory extraction like any normal end.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="SessionSummaryScheduler"/> rather than folded into it: that
/// scheduler skips a project entirely when <see cref="ProjectSettingsDto.SessionSummaryIntervalMinutes"/>
/// is &lt;= 0, and cleanup must not silently depend on the unrelated summary feature being enabled.
/// Same fire-and-forget-per-project shape as <see cref="ReindexSchedulerService"/>: one project (or
/// one session within it) failing must never block another's turn or delay the next poll.
/// </remarks>
public sealed class StaleSessionScheduler(
    IServiceScopeFactory scopeFactory,
    TimeProvider? timeProvider = null,
    ILogger<StaleSessionScheduler>? logger = null)
    : PeriodicScheduler(PollInterval, timeProvider ?? TimeProvider.System, logger ?? NullLogger<StaleSessionScheduler>.Instance)
{
    private const string StaleReason = "stale-timeout";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
    private const int MaxSessionsPerProjectPerPoll = 100;

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    // Guards against reprocessing a session already ended this poll before its DB write (from a
    // prior poll's EndSessionAsync) is visible to the next ListByProjectAsync read - same shape as
    // SessionSummaryScheduler's _lastEnqueuedAt guard. Reset on restart; worst case is one extra
    // (harmless, idempotent) EndSessionAsync call.
    private readonly HashSet<Guid> _endedThisPoll = new();

    protected internal override async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var projectRepository = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var settingsService = scope.ServiceProvider.GetRequiredService<IProjectSettingsService>();
        var sessionRepository = scope.ServiceProvider.GetRequiredService<ISessionRepository>();
        var engine = scope.ServiceProvider.GetRequiredService<IContextEngine>();

        _endedThisPoll.Clear();

        var projects = await projectRepository.ListAsync(ct).ConfigureAwait(false);

        foreach (var project in projects)
        {
            try
            {
                await MaybeEndStaleSessionsAsync(project.Id, settingsService, sessionRepository, engine, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "Failed to evaluate stale-session cleanup for project {ProjectId}.", project.Id);
            }
        }
    }

    private async Task MaybeEndStaleSessionsAsync(
        Guid projectId,
        IProjectSettingsService settingsService,
        ISessionRepository sessionRepository,
        IContextEngine engine,
        CancellationToken ct)
    {
        var settings = await settingsService.GetAsync(projectId, ct).ConfigureAwait(false);
        if (settings.StaleSessionTimeoutMinutes <= 0)
        {
            return;
        }

        var timeout = TimeSpan.FromMinutes(settings.StaleSessionTimeoutMinutes);
        var sessions = await sessionRepository.ListByProjectAsync(projectId, MaxSessionsPerProjectPerPoll, ct).ConfigureAwait(false);

        foreach (var item in sessions)
        {
            var session = item.Session;
            if (session.Status != SessionStatus.Active || _endedThisPoll.Contains(session.Id))
            {
                continue;
            }

            try
            {
                var events = await sessionRepository.GetEventsAsync(projectId, session.Id, ct).ConfigureAwait(false);
                var lastActivity = events.Count > 0 ? events[^1].Timestamp : session.StartedAt;
                if (TimeProvider.GetUtcNow() - lastActivity < timeout)
                {
                    continue;
                }

                _endedThisPoll.Add(session.Id);
                await engine.EndSessionAsync(projectId, session.Id, ct, StaleReason).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogError(ex, "Failed to end stale session {SessionId}.", session.Id);
            }
        }
    }
}
