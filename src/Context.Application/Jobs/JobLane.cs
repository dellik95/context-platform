using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Context.Application.Jobs;

/// <summary>
/// Single-threaded claim loop for one project's queue (or the host-wide queue when
/// <paramref name="projectId"/> is null), bounded by a <see cref="JobWorkerPool"/>.
/// </summary>
internal sealed class JobLane(
    Guid? projectId,
    bool light,
    JobWorkerPool pool,
    IServiceScopeFactory scopeFactory,
    Func<Job, CancellationToken, Task> processClaimedJobAsync,
    Action<IReadOnlyList<Guid>, CancellationToken> kickWaitingLanes,
    ILogger logger)
{
    private readonly Guid _key = projectId ?? Guid.Empty;
    private readonly JobLaneFilter _filter = light
        ? new JobLaneFilter(OnlyTypes: JobRetryPolicy.Light)
        : new JobLaneFilter(ExceptTypes: JobRetryPolicy.Light);
    private int _draining;
    private int _kickPending;

    public void Kick(CancellationToken ct)
    {
        Interlocked.Exchange(ref _kickPending, 1);
        if (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
        {
            _ = Task.Run(() => DrainLoopAsync(ct), CancellationToken.None);
        }
    }

    private async Task DrainLoopAsync(CancellationToken ct)
    {
        do
        {
            Volatile.Write(ref _kickPending, 0);
            try
            {
                await DrainAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                Volatile.Write(ref _draining, 0);
            }
        }
        while (Volatile.Read(ref _kickPending) == 1
            && !ct.IsCancellationRequested
            && Interlocked.CompareExchange(ref _draining, 1, 0) == 0);
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (!pool.TryAcquire(_key, out var borrowed))
            {
                return;
            }

            Job? job;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
                job = await jobRepository.DequeueNextForProjectAsync(projectId, _filter, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ReleaseSlot(borrowed, idle: false, ct);
                return;
            }
            catch (Exception ex)
            {
                ReleaseSlot(borrowed, idle: false, ct);
                logger.LogError(ex,
                    "Unhandled error while claiming the next job for project {ProjectId}; the next signal or poll tick will try again.",
                    projectId);
                return;
            }

            if (job is null)
            {
                ReleaseSlot(borrowed, idle: true, ct);
                return;
            }

            _ = processClaimedJobAsync(job, ct).ContinueWith(
                _ =>
                {
                    ReleaseSlot(borrowed, idle: false, ct);
                    if (!ct.IsCancellationRequested)
                    {
                        Kick(ct);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void ReleaseSlot(bool borrowed, bool idle, CancellationToken ct) =>
        kickWaitingLanes(pool.Release(_key, borrowed, idle), ct);
}
