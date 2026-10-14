using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Scheduling;

/// <summary>
/// Deletes terminal (Done/Failed) <c>Jobs</c> rows older than <see cref="RetentionPeriod"/>.
/// Indexing a repository enqueues an EmbedChunk job per batch of chunks, and every one of them leaves a
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

    /// <summary>A finished ProcessHook job carries a full hook payload (a PostToolUse one holds the tool's whole
    /// response) that the HookInvocation row already keeps, so it ages out sooner than other jobs.
    /// Failed ones stay for <see cref="RetentionPeriod"/>: they are the only record of an unprocessed hook.</summary>
    public static readonly TimeSpan DoneHookRetentionPeriod = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    protected internal override async Task PollOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        var now = TimeProvider.GetUtcNow();
        var cutoff = now - RetentionPeriod;
        var deleted = await jobRepository
            .DeleteFinishedOlderThanAsync(now - DoneHookRetentionPeriod, new JobRetentionFilter(JobType.ProcessHook, OnlyStatus: JobStatus.Done), ct)
            .ConfigureAwait(false);
        deleted += await jobRepository.DeleteFinishedOlderThanAsync(cutoff, JobRetentionFilter.All, ct).ConfigureAwait(false);

        if (deleted > 0)
        {
            Logger.LogInformation("Job retention sweep removed {Deleted} terminal jobs finished before {Cutoff:O}.", deleted, cutoff);
        }
    }
}
