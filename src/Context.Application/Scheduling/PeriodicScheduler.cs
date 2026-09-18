using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Context.Application.Scheduling;

/// <summary>
/// Base for the project-polling background services. Each subclass only implements one poll;
/// the loop, the interval and — critically — the top-level exception guard live here.
/// </summary>
/// <remarks>
/// The guard is the point. A <see cref="BackgroundService"/> whose <c>ExecuteAsync</c> throws is
/// not restarted, and under the default <see cref="HostOptions.BackgroundServiceExceptionBehavior"/>
/// it takes the host down with it; without the guard a single transient "database is locked" from
/// the project list would permanently stop scheduling for the whole process.
/// </remarks>
public abstract class PeriodicScheduler(TimeSpan pollInterval, TimeProvider timeProvider, ILogger logger) : BackgroundService
{
    protected TimeProvider TimeProvider { get; } = timeProvider;

    protected ILogger Logger { get; } = logger;

    /// <summary>
    /// One poll. Implementations resolve their own scope; failures are logged, not fatal.
    /// <c>protected internal</c> so the scheduler tests (see <c>InternalsVisibleTo</c>) can drive a
    /// single poll deterministically instead of waiting on the interval.
    /// </summary>
    protected internal abstract Task PollOnceAsync(CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(pollInterval, TimeProvider);

        do
        {
            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{Scheduler} poll failed; the next poll runs on schedule.", GetType().Name);
            }
        }
        while (await WaitForNextTickAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
