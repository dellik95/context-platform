using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Scheduling;

/// <summary>
/// Deletes terminal (Done/Failed) <c>Jobs</c> rows older than <see cref="RetentionPeriod"/>.
/// Indexing a repository enqueues one EmbedChunk job per chunk, and every one of them leaves a
/// permanent row behind with nothing downstream that ever reads it again — the System tab's manual
/// cleanup was the only thing that removed them, so a host nobody clicks buttons on grew this table
/// without bound and paid for it on every <see cref="IJobRepository.DequeueNextAsync"/>.
/// </summary>
/// <remarks>
/// Host-wide rather than per-project (unlike <see cref="HookInvocationRetentionScheduler"/>): a job
/// may have no <c>ProjectId</c> at all, and the window here is an operational property of the queue
/// rather than something a project owner would reasonably tune per project. Failed jobs are kept
/// for the same window as Done ones deliberately — they are past their retry budget, so their value
/// is diagnostic, and a two-week-old permanent failure is not being diagnosed from this table.
/// </remarks>
public sealed class JobRetentionScheduler(
    IServiceScopeFactory scopeFactory,
    TimeProvider? timeProvider = null,
    ILogger<JobRetentionScheduler>? logger = null)
    : PeriodicScheduler(PollInterval, timeProvider ?? TimeProvider.System, logger ?? NullLogger<JobRetentionScheduler>.Instance)
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(7);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    protected internal override async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        var cutoff = TimeProvider.GetUtcNow() - RetentionPeriod;
        var deleted = await jobRepository.DeleteFinishedOlderThanAsync(cutoff, ct).ConfigureAwait(false);

        if (deleted > 0)
        {
            Logger.LogInformation("Job retention sweep removed {Deleted} terminal jobs finished before {Cutoff:O}.", deleted, cutoff);
        }
    }
}
