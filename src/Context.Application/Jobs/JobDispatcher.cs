using System.Text.Json;
using System.Threading.Channels;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Jobs;

/// <summary>
/// Durable-log-plus-in-process-dispatcher <see cref="IJobQueue"/> implementation, per the task
/// brief §2. <see cref="IJobRepository"/> is the durable log (every
/// job is a <see cref="Job"/> row, and nothing is ever lost across a process restart);
/// <see cref="Channel{T}"/> of job ids is the live, in-memory work queue that gives workers
/// something to await instead of polling. Registered as a singleton (see
/// <c>ServiceCollectionExtensions.AddContextApplication</c>) implementing both
/// <see cref="IJobQueue"/> (so any scoped consumer can enqueue work) and
/// <see cref="IHostedService"/> (so <c>Context.Host</c> only has to
/// <c>services.AddHostedService(sp => sp.GetRequiredService&lt;JobDispatcher&gt;())</c> to get the
/// background worker pool started/stopped with the app's own lifetime — the
/// "hosted-service-friendly" part of the brief).
/// </summary>
/// <remarks>
/// <b>Per-job DI scope:</b> <see cref="Handlers.IJobHandler"/> implementations depend on Scoped
/// repositories (EF Core), and this dispatcher is a singleton, so every job is processed inside
/// its own freshly-created <see cref="IServiceScope"/> (see <see cref="ProcessJobAsync"/>) —
/// exactly as documented on <see cref="Handlers.IJobHandler"/> itself.
/// </remarks>
/// <remarks>
/// <b>Retry scheduling:</b> the channel literally carries the id of the job to run next (matching
/// the brief's "writes the Id to the channel" / "loads the Job" wording), not just a wake-up
/// signal. A failed job that still has attempts left is put back to <see cref="JobStatus.Pending"/>
/// with <see cref="Job.NextRetryAt"/> set, and its id is re-written to the channel after an
/// in-process <see cref="Task.Delay(TimeSpan,CancellationToken)"/> matching the backoff — so the
/// live queue, not just the durable log, is what actually drives the retry. A crash-recovered
/// (stale <see cref="JobStatus.Running"/>) or previously-<see cref="JobStatus.Pending"/> job whose
/// <see cref="Job.NextRetryAt"/> is still in the future gets the same delayed-re-enqueue treatment
/// from <see cref="HydrateOnStartupAsync"/>, so a restart never loses a pending backoff window.
/// </remarks>
public sealed class JobDispatcher : IJobQueue, IHostedService, IAsyncDisposable
{
    /// <summary>Per JobType max attempts before a failure is final (spec: 5 for EmbedChunk, 3 for everything else).</summary>
    private const int DefaultMaxAttempts = 3;
    private const int EmbedChunkMaxAttempts = 5;

    /// <summary>A Running job found at startup can only mean the previous process died mid-job; always requeue it.</summary>
    private static readonly TimeSpan RunningIsAlwaysStaleAtStartup = TimeSpan.Zero;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobDispatcher> _logger;
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private CancellationTokenSource? _lifetimeCts;
    private Task? _processingTask;

    // JobDispatcher is deliberately registered under three singleton service types
    // (JobDispatcher, IJobQueue, IHostedService) that all resolve to this same instance
    // (see ServiceCollectionExtensions.AddJobDispatcher). Microsoft.Extensions.DependencyInjection
    // tracks each singleton call site's resolved disposable separately, so a container disposing
    // its root ServiceProvider calls DisposeAsync on this same object once per call site — i.e.
    // up to three times. Both StopAsync and DisposeAsync must therefore tolerate repeat calls.
    private int _stopped;
    private int _disposed;

    public JobDispatcher(IServiceScopeFactory scopeFactory, ILogger<JobDispatcher>? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger ?? NullLogger<JobDispatcher>.Instance;
    }

    public async Task<Guid> EnqueueAsync(JobType type, object payload, int priority = 0, CancellationToken ct = default)
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Type = type,
            PayloadJson = JsonSerializer.Serialize(payload, payload.GetType()),
            Status = JobStatus.Pending,
            Priority = priority,
            Attempts = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };

        using (var scope = _scopeFactory.CreateScope())
        {
            var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            job = await jobRepository.EnqueueAsync(job, ct).ConfigureAwait(false);
        }

        await _channel.Writer.WriteAsync(job.Id, ct).ConfigureAwait(false);
        return job.Id;
    }

    /// <summary>
    /// Loads every currently <see cref="JobStatus.Pending"/> job, plus every
    /// <see cref="JobStatus.Running"/> job (which — this being a single in-process dispatcher with
    /// no other writer of that status — can only mean the previous process crashed mid-job, so it
    /// is unconditionally treated as crashed and reset to Pending), and feeds them into the live
    /// channel so they resume being processed. Call once at startup, before or right after
    /// <see cref="StartProcessingAsync"/> (both are safe in either order: the channel buffers).
    /// </summary>
    public async Task HydrateOnStartupAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        var pending = await jobRepository.ListByStatusAsync(JobStatus.Pending, ct).ConfigureAwait(false);
        var staleRunning = await jobRepository.ListByStatusAsync(JobStatus.Running, ct).ConfigureAwait(false);

        foreach (var job in staleRunning)
        {
            ct.ThrowIfCancellationRequested();

            _logger.LogWarning(
                "Job {JobId} ({JobType}) was left Running at startup — a previous process must have crashed mid-job; resetting to Pending.",
                job.Id, job.Type);

            job.Status = JobStatus.Pending;
            job.NextRetryAt = DateTimeOffset.UtcNow + RunningIsAlwaysStaleAtStartup;
            await jobRepository.UpdateAsync(job, ct).ConfigureAwait(false);
        }

        var toResume = pending.Concat(staleRunning)
            .DistinctBy(j => j.Id)
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.CreatedAt);

        var now = DateTimeOffset.UtcNow;
        foreach (var job in toResume)
        {
            ct.ThrowIfCancellationRequested();
            await ScheduleOrEnqueueAsync(job.Id, job.NextRetryAt, now, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drains the live channel with a bounded-concurrency pool of <see cref="Environment.ProcessorCount"/>
    /// workers. Runs until <paramref name="ct"/> is cancelled or the channel is completed (on
    /// <see cref="StopAsync"/>). One job type's failure never stops another's processing — every
    /// job is wrapped in its own try/catch inside <see cref="ProcessJobAsync"/>, and a worker loop
    /// only ever exits on cancellation.
    /// </summary>
    public Task StartProcessingAsync(CancellationToken ct)
    {
        var workerCount = Math.Max(1, Environment.ProcessorCount);
        var workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(() => WorkerLoopAsync(ct), ct);
        }

        return Task.WhenAll(workers);
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var jobId in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await ProcessJobAsync(jobId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // ProcessJobAsync already catches and records handler failures against the Job
                    // row itself; reaching here means something failed outside that (e.g. the
                    // repository call to load/mark the job). Never let it take the whole worker
                    // down — one bad job must not stop every other job type from progressing.
                    _logger.LogError(ex, "Unhandled error in job dispatcher worker while processing job {JobId}.", jobId);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task ProcessJobAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        var job = await jobRepository.GetByIdAsync(jobId, ct).ConfigureAwait(false);
        if (job is null)
        {
            _logger.LogWarning("Job {JobId} was signalled for processing but no longer exists.", jobId);
            return;
        }

        if (job.Status is JobStatus.Done)
        {
            return; // duplicate signal (e.g. re-enqueued while already mid-flight elsewhere)
        }

        job.Status = JobStatus.Running;
        job.StartedAt = DateTimeOffset.UtcNow;
        job.Attempts += 1;
        await jobRepository.UpdateAsync(job, ct).ConfigureAwait(false);

        var handler = scope.ServiceProvider.GetServices<Handlers.IJobHandler>().FirstOrDefault(h => h.Type == job.Type);
        if (handler is null)
        {
            job.Status = JobStatus.Failed;
            job.LastError = $"No {nameof(Handlers.IJobHandler)} is registered for job type {job.Type}.";
            job.FinishedAt = DateTimeOffset.UtcNow;
            await jobRepository.UpdateAsync(job, ct).ConfigureAwait(false);
            _logger.LogError("No handler registered for job type {JobType} (job {JobId}); marking Failed.", job.Type, job.Id);
            return;
        }

        try
        {
            await handler.HandleAsync(job, ct).ConfigureAwait(false);

            job.Status = JobStatus.Done;
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.LastError = null;
            job.NextRetryAt = null;
            await jobRepository.UpdateAsync(job, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host is shutting down mid-job, not a job failure: put it back exactly as it was
            // found so HydrateOnStartupAsync picks it up again next run, without burning an
            // attempt. Deliberately CancellationToken.None here - ct is already cancelled at this
            // point, and this compensating write must still land (otherwise the job is merely left
            // Running, which HydrateOnStartupAsync would still recover on the next start, just less
            // cleanly - see remarks).
            job.Status = JobStatus.Pending;
            job.Attempts = Math.Max(0, job.Attempts - 1);
            job.StartedAt = null;
            await jobRepository.UpdateAsync(job, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await HandleFailureAsync(jobRepository, job, ex, ct).ConfigureAwait(false);
        }
    }

    private async Task HandleFailureAsync(IJobRepository jobRepository, Job job, Exception ex, CancellationToken ct)
    {
        var maxAttempts = job.Type == JobType.EmbedChunk ? EmbedChunkMaxAttempts : DefaultMaxAttempts;
        var message = ex.ToString();
        job.LastError = message.Length > 4000 ? message[..4000] : message;

        if (job.Attempts >= maxAttempts)
        {
            job.Status = JobStatus.Failed;
            job.FinishedAt = DateTimeOffset.UtcNow;
            job.NextRetryAt = null;
            await jobRepository.UpdateAsync(job, ct).ConfigureAwait(false);

            _logger.LogError(ex,
                "Job {JobId} ({JobType}) failed permanently after {Attempts}/{MaxAttempts} attempts.",
                job.Id, job.Type, job.Attempts, maxAttempts);
            return;
        }

        // Exponential backoff: 2s, 4s, 8s, ... capped at 5 minutes so a mis-tuned attempt count
        // never leaves a job in limbo for an unreasonable time.
        var backoff = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, job.Attempts + 1)));
        job.Status = JobStatus.Pending;
        job.NextRetryAt = DateTimeOffset.UtcNow + backoff;
        await jobRepository.UpdateAsync(job, ct).ConfigureAwait(false);

        _logger.LogWarning(ex,
            "Job {JobId} ({JobType}) failed (attempt {Attempts}/{MaxAttempts}); retrying at {NextRetryAt:O}.",
            job.Id, job.Type, job.Attempts, maxAttempts, job.NextRetryAt);

        ScheduleRetry(job.Id, backoff);
    }

    /// <summary>
    /// Writes <paramref name="jobId"/> to the live channel immediately if it's already runnable,
    /// or schedules a delayed write for when <paramref name="nextRetryAt"/> elapses. Used by both
    /// <see cref="HydrateOnStartupAsync"/> (recovering jobs across a restart) and
    /// <see cref="HandleFailureAsync"/> (via <see cref="ScheduleRetry"/>) so both paths share one
    /// implementation of "respect NextRetryAt".
    /// </summary>
    private async Task ScheduleOrEnqueueAsync(Guid jobId, DateTimeOffset? nextRetryAt, DateTimeOffset now, CancellationToken ct)
    {
        if (nextRetryAt is { } retryAt && retryAt > now)
        {
            ScheduleRetry(jobId, retryAt - now);
            return;
        }

        await _channel.Writer.WriteAsync(jobId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fire-and-forget delayed re-enqueue, tied to the dispatcher's own lifetime token (not the
    /// caller's, which may already be gone by the time the delay elapses — e.g. a job's original
    /// per-request cancellation token). Any failure here (including cancellation on shutdown) is
    /// swallowed: on shutdown the job simply stays Pending in the durable log and
    /// <see cref="HydrateOnStartupAsync"/> picks it up on the next start.
    /// </summary>
    private void ScheduleRetry(Guid jobId, TimeSpan delay)
    {
        var lifetimeToken = _lifetimeCts?.Token ?? CancellationToken.None;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, lifetimeToken).ConfigureAwait(false);
                await _channel.Writer.WriteAsync(jobId, lifetimeToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutting down before the retry was due - fine, the durable log still has it Pending.
            }
            catch (ChannelClosedException)
            {
                // Same as above, raced against StopAsync completing the channel.
            }
        }, lifetimeToken);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetimeCts = new CancellationTokenSource();

        await HydrateOnStartupAsync(_lifetimeCts.Token).ConfigureAwait(false);

        // Long-running: intentionally not awaited so the host's StartAsync returns promptly.
        _processingTask = StartProcessingAsync(_lifetimeCts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetimeCts is null || Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _lifetimeCts.Cancel();
        _channel.Writer.TryComplete();

        if (_processingTask is not null)
        {
            try
            {
                await _processingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Either our own lifetime cancellation or the host's shutdown timeout - either way, done waiting.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetimeCts is null || Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifetimeCts.Dispose();
    }
}
