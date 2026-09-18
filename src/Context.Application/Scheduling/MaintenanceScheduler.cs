using Context.Application.Jobs;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Scheduling;

/// <summary>
/// Enqueues the two host-wide maintenance jobs — <see cref="JobType.BackupDb"/> and
/// <see cref="JobType.DecayMemory"/> — once a day. Both handlers were complete and registered but
/// nothing ever enqueued either one, so neither had run in production: the database had no
/// automatic backup at all, and unreinforced Working memories never lost confidence.
/// </summary>
/// <remarks>
/// The daily cadence is derived from the queue (<see cref="IJobRepository.GetLastEnqueuedAtAsync"/>),
/// not from a <c>PeriodicTimer</c> tick, because process uptime is the wrong clock: a host that
/// restarts more often than once a day would never reach a 24-hour tick and would silently never
/// back up. Polling hourly and asking "has one been enqueued in the last day?" is restart-safe and
/// idempotent — a duplicate enqueue would only cost one extra snapshot, and the lookup window is
/// well inside <see cref="JobRetentionScheduler.RetentionPeriod"/>, so the evidence is always there.
/// </remarks>
public sealed class MaintenanceScheduler(
    IServiceScopeFactory scopeFactory,
    TimeProvider? timeProvider = null,
    ILogger<MaintenanceScheduler>? logger = null)
    : PeriodicScheduler(PollInterval, timeProvider ?? TimeProvider.System, logger ?? NullLogger<MaintenanceScheduler>.Instance)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    public static readonly TimeSpan MaintenanceInterval = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    protected internal override async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();

        var due = TimeProvider.GetUtcNow() - MaintenanceInterval;

        await EnqueueIfDueAsync(jobs, queue, JobType.BackupDb, new BackupDbPayload(), due, ct).ConfigureAwait(false);
        await EnqueueIfDueAsync(jobs, queue, JobType.DecayMemory, new DecayMemoryPayload(), due, ct).ConfigureAwait(false);
    }

    private async Task EnqueueIfDueAsync(
        IJobRepository jobs,
        IJobQueue queue,
        JobType type,
        object payload,
        DateTimeOffset due,
        CancellationToken ct)
    {
        var lastEnqueuedAt = await jobs.GetLastEnqueuedAtAsync(type, ct).ConfigureAwait(false);
        if (lastEnqueuedAt is { } last && last > due)
        {
            return;
        }

        await queue.EnqueueAsync(type, payload, projectId: null, ct: ct).ConfigureAwait(false);
        Logger.LogInformation("Enqueued daily {JobType} maintenance job (last run: {LastRun}).", type, lastEnqueuedAt?.ToString("O") ?? "never");
    }
}
