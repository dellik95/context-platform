using System.Collections.Concurrent;
using System.Text.Json;
using Context.Application.Notifications;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Context.Application.Jobs;

/// <summary>
/// Durable-log-plus-signal <see cref="IJobQueue"/> implementation, per the task brief §2.
/// <see cref="IJobRepository"/> is the durable log AND the sole source of truth for which job runs
/// next: <see cref="IJobRepository.DequeueNextAsync"/> atomically claims the highest-priority
/// runnable job (flips it <c>Pending</c> -&gt; <c>Running</c> inside one transaction), so claiming is
/// safe even when more than one <see cref="JobDispatcher"/> process shares the same database.
/// <see cref="IJobSignal"/> is only ever a hint to go check the database again -- it never carries a
/// specific job id -- which is what makes multi-process operation correct: two processes racing to
/// claim after the same signal simply have one of them get the job and the other get <c>null</c>
/// back, no coordination beyond what the database's own transaction already provides. Registered as
/// a singleton (see <c>ServiceCollectionExtensions.AddContextApplication</c>) implementing both
/// <see cref="IJobQueue"/> (so any scoped consumer can enqueue work) and <see cref="IHostedService"/>
/// (so <c>Context.Dashboard.Server</c> only has to
/// <c>services.AddHostedService(sp => sp.GetRequiredService&lt;JobDispatcher&gt;())</c> to get the
/// background worker pool started/stopped with the app's own lifetime).
/// </summary>
/// <remarks>
/// <b>Per-job DI scope:</b> <see cref="Handlers.IJobHandler"/> implementations depend on Scoped
/// repositories (EF Core), and this dispatcher is a singleton, so every job is processed inside its
/// own freshly-created <see cref="IServiceScope"/> (see <see cref="ProcessClaimedJobAsync"/>) --
/// exactly as documented on <see cref="Handlers.IJobHandler"/> itself.
/// </remarks>
/// <remarks>
/// <b>One lane per project:</b> every project (plus one lane for host-wide jobs with no
/// <see cref="Job.ProjectId"/>) gets its own <see cref="JobLane"/> — its own claim loop over
/// <see cref="IJobRepository.DequeueNextForProjectAsync"/> and its own
/// <see cref="JobDispatcherOptions.WorkersPerProject"/> slots. A single shared pool let one
/// project's reindex or a hung LLM call occupy every slot and stall every other project's hooks,
/// summaries and extraction behind it; now a project can only exhaust its own lane. Lanes are
/// created lazily from <see cref="IJobRepository.ListProjectsWithPendingJobsAsync"/> (on every
/// <see cref="IJobSignal"/> wake-up and poll tick) and kicked directly by <see cref="EnqueueAsync"/>.
/// <para>
/// Per-lane slots stay small: handlers spend their time in blocking file and SQLite I/O rather than
/// on the CPU, and Microsoft.Data.Sqlite has no true async path, so an in-flight job generally means
/// an occupied thread-pool thread (the host raises the pool's floor for exactly that reason).
/// </para>
/// </remarks>
/// <remarks>
/// <b>Retry scheduling:</b> a failed job with attempts remaining goes back to
/// <see cref="JobStatus.Pending"/> with <see cref="Job.NextRetryAt"/> set, same as before. There is
/// no precise per-job re-enqueue timer any more -- <see cref="IJobRepository.DequeueNextAsync"/>
/// already filters on <c>NextRetryAt</c>, so the fallback poll timer (<see cref="PollInterval"/>)
/// picks up an elapsed backoff on its own, within one poll interval. That small added latency
/// (seconds, against a backoff already measured in seconds-to-minutes) is the price of a mechanism
/// that also works correctly across more than one process, where a per-process in-memory timer
/// could not.
/// </remarks>
public sealed class JobDispatcher : IJobQueue, IHostedService, IAsyncDisposable
{
    /// <summary>Per JobType max attempts before a failure is final (spec: 5 for EmbedChunk, 3 for everything else).</summary>
    private const int DefaultMaxAttempts = 3;
    private const int EmbedChunkMaxAttempts = 5;

    /// <summary>A Running job found at startup can only mean the previous process died mid-job; always requeue it.</summary>
    private static readonly TimeSpan RunningIsAlwaysStaleAtStartup = TimeSpan.Zero;

    /// <summary>Fallback cadence for noticing a job whose backoff (<see cref="Job.NextRetryAt"/>)
    /// has elapsed, or a signal that never arrived (dropped notification, no Redis configured and
    /// this is a different process, etc.) -- see this type's own remarks on retry scheduling.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long <see cref="ForceRunAsync"/> waits for a cancelled execution to unwind. A handler
    /// blocked in non-cancellable I/O may never get there; it is superseded either way.</summary>
    private static readonly TimeSpan SupersedeGrace = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobDispatcher> _logger;
    private readonly IJobCountsNotifier _jobCountsNotifier;
    private readonly IJobSignal _jobSignal;
    private readonly int _workersPerProject;

    /// <summary>Keyed by project id, <see cref="Guid.Empty"/> for the host-wide lane
    /// (<see cref="ConcurrentDictionary{TKey,TValue}"/> rejects a null key).</summary>
    private readonly ConcurrentDictionary<Guid, JobLane> _lanes = new();

    private readonly ConcurrentDictionary<Guid, JobExecution> _executions = new();

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

    public JobDispatcher(
        IServiceScopeFactory scopeFactory,
        ILogger<JobDispatcher>? logger = null,
        IJobCountsNotifier? jobCountsNotifier = null,
        IJobSignal? jobSignal = null,
        IOptions<JobDispatcherOptions>? options = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger ?? NullLogger<JobDispatcher>.Instance;
        _jobCountsNotifier = jobCountsNotifier ?? NullJobCountsNotifier.Instance;
        _jobSignal = jobSignal ?? new InMemoryJobSignal();
        _workersPerProject = Math.Max(1, (options?.Value ?? new JobDispatcherOptions()).WorkersPerProject);
    }

    /// <summary>
    /// Re-queries <paramref name="projectId"/>'s (Pending, Running, Failed, Done) breakdown and
    /// pushes it via <see cref="IJobCountsNotifier"/> — called after every point in this file where
    /// a job's <see cref="JobStatus"/> actually changes, so the Dashboard's System tab can update
    /// its stat tiles live instead of polling. A host-wide maintenance job (<paramref name="projectId"/>
    /// is null) has no project tile to update, so this is a no-op for it. A notifier failure (e.g. a
    /// transient SignalR hub error) must never take job processing down with it, so every exception
    /// here is swallowed.
    /// </summary>
    private async Task PublishJobCountsAsync(Guid? projectId, CancellationToken ct)
    {
        if (projectId is not { } id)
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var counts = await jobRepository.CountByStatusAsync(id, ct).ConfigureAwait(false);
            await _jobCountsNotifier.NotifyAsync(
                id, new JobCounts(counts.Pending, counts.Running, counts.Failed, counts.Done), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish job counts after a status change.");
        }
    }

    public async Task<Guid> EnqueueAsync(JobType type, object payload, Guid? projectId = null, int priority = 0, CancellationToken ct = default)
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            Type = type,
            ProjectId = projectId,
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

        KickLane(projectId);
        await _jobSignal.PublishAsync(ct).ConfigureAwait(false);
        await PublishJobCountsAsync(projectId, ct).ConfigureAwait(false);
        return job.Id;
    }

    /// <summary>
    /// Manual re-trigger for a permanently <see cref="JobStatus.Failed"/> job (System tab "retry"
    /// button) — see <see cref="IJobQueue.RequeueAsync"/>. Resets the durable row to a fresh attempt
    /// and publishes a signal so it's picked up promptly rather than waiting for the next poll tick.
    /// </summary>
    public async Task RequeueAsync(Guid jobId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        var job = await jobRepository.GetByIdAsync(jobId, ct).ConfigureAwait(false);
        if (job is null)
        {
            throw new JobNotFoundException(jobId);
        }

        if (job.Status != JobStatus.Failed)
        {
            throw new JobStatusConflictException(jobId, job.Status, "retried");
        }

        job.Status = JobStatus.Pending;
        job.Attempts = 0;
        job.NextRetryAt = null;
        job.StartedAt = null;
        job.FinishedAt = null;
        await jobRepository.UpdateAsync(job, ct).ConfigureAwait(false);

        _logger.LogInformation("Job {JobId} ({JobType}) manually requeued after permanent failure.", job.Id, job.Type);

        KickLane(job.ProjectId);
        await _jobSignal.PublishAsync(ct).ConfigureAwait(false);
        await PublishJobCountsAsync(job.ProjectId, ct).ConfigureAwait(false);
    }

    /// <summary>See <see cref="IJobQueue.ForceRunAsync"/>. A Running job owned by another host process
    /// is taken over too: with no heartbeat, this process can't tell it apart from an orphaned row.</summary>
    public async Task ForceRunAsync(Guid jobId, CancellationToken ct = default)
    {
        if (_lifetimeCts is not { } cts || Volatile.Read(ref _stopped) != 0)
        {
            throw new InvalidOperationException("The job dispatcher is not running.");
        }

        if (_executions.TryGetValue(jobId, out var inFlight))
        {
            inFlight.Supersede();
            try
            {
                await inFlight.Completion.WaitAsync(SupersedeGrace, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Job {JobId} did not stop within {Grace} of being cancelled; abandoning it.", jobId, SupersedeGrace);
            }
        }

        using var scope = _scopeFactory.CreateScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        var job = await jobRepository.GetByIdAsync(jobId, ct).ConfigureAwait(false) ?? throw new JobNotFoundException(jobId);
        if (job.Status == JobStatus.Done)
        {
            throw new JobStatusConflictException(jobId, job.Status, "force-run");
        }

        var claimed = await jobRepository.ForceClaimAsync(jobId, job.Status, ct).ConfigureAwait(false);
        if (claimed is null)
        {
            var current = await jobRepository.GetByIdAsync(jobId, ct).ConfigureAwait(false) ?? throw new JobNotFoundException(jobId);
            throw new JobStatusConflictException(jobId, current.Status, "force-run");
        }

        _logger.LogWarning("Job {JobId} ({JobType}) force-run from {Status}.", claimed.Id, claimed.Type, job.Status);

        _ = ProcessClaimedJobAsync(claimed, cts.Token);
        await PublishJobCountsAsync(claimed.ProjectId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resets every currently <see cref="JobStatus.Running"/> job back to <see cref="JobStatus.Pending"/>:
    /// this being a single-writer-per-row model with no heartbeat, a job still marked Running at
    /// startup can only mean whichever process claimed it died mid-job. Deliberately does NOT
    /// re-enqueue anything itself any more -- <see cref="KickPendingLanesAsync"/>, started right after this by
    /// <see cref="StartAsync"/>, claims everything runnable (including what this just reset) on its
    /// own first pass.
    /// </summary>
    private async Task HydrateOnStartupAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        var staleRunning = await jobRepository.ListByStatusAsync(JobStatus.Running, null, ct).ConfigureAwait(false);
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
    }

    /// <summary>
    /// The two long-running loops that keep this process claiming work: <see cref="ListenLoopAsync"/>
    /// reacts to <see cref="IJobSignal"/> notifications (near-instant), <see cref="PollLoopAsync"/>
    /// is the fallback that catches anything a dropped/never-sent signal would otherwise miss (an
    /// elapsed retry backoff, a signal lost before this process was listening). Both ultimately call
    /// the same <see cref="KickPendingLanesAsync"/>, which is safe to run concurrently with itself.
    /// </summary>
    private Task StartProcessingAsync(CancellationToken ct) =>
        Task.WhenAll(ListenLoopAsync(ct), PollLoopAsync(ct));

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var _ in _jobSignal.ListenAsync(ct).ConfigureAwait(false))
            {
                await KickPendingLanesAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // A signal transport failure (e.g. Redis unreachable for the whole process lifetime,
            // not just a transient reconnect) must never take job processing down with it --
            // PollLoopAsync's fallback timer keeps jobs moving on its own, just up to PollInterval
            // slower without this loop's near-instant wake-ups.
            _logger.LogError(ex, "Job signal listener stopped unexpectedly; falling back to polling every {PollInterval}.", PollInterval);
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            // Kick once immediately (covers whatever HydrateOnStartupAsync just reset, plus
            // anything already Pending from before this process started) rather than waiting out
            // the first full interval.
            await KickPendingLanesAsync(ct).ConfigureAwait(false);

            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await KickPendingLanesAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    /// <summary>Wakes the lane of every project that has Pending work, creating lanes as needed.</summary>
    private async Task KickPendingLanesAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid?> projectIds;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            projectIds = await jobRepository.ListProjectsWithPendingJobsAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error while looking for pending jobs; the next signal or poll tick will try again.");
            return;
        }

        foreach (var projectId in projectIds)
        {
            KickLane(projectId);
        }
    }

    private void KickLane(Guid? projectId)
    {
        if (_lifetimeCts is not { } cts || Volatile.Read(ref _stopped) != 0)
        {
            // Not started yet: StartAsync's first poll pass picks this job up.
            return;
        }

        _lanes.GetOrAdd(projectId ?? Guid.Empty, static (_, state) => new JobLane(state.Dispatcher, state.ProjectId), (Dispatcher: this, ProjectId: projectId))
            .Kick(cts.Token);
    }

    /// <summary>
    /// One project's queue (or the host-wide one). Claims are single-threaded per lane — at most one
    /// <see cref="IJobRepository.DequeueNextForProjectAsync"/> in flight for it — and dispatch is
    /// bounded by the lane's own slots. A finishing job frees a slot and re-kicks its own lane, so a
    /// backlog drains without waiting on a signal or poll tick.
    /// </summary>
    private sealed class JobLane(JobDispatcher owner, Guid? projectId)
    {
        private readonly SemaphoreSlim _slots = new(owner._workersPerProject);
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
            // A Kick landing after DrainAsync's last empty claim saw _draining still set and left
            // it to us; without this re-check that wake-up would be lost.
            while (Volatile.Read(ref _kickPending) == 1
                && !ct.IsCancellationRequested
                && Interlocked.CompareExchange(ref _draining, 1, 0) == 0);
        }

        private async Task DrainAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_slots.Wait(0))
                {
                    return;
                }

                Job? job;
                try
                {
                    using var scope = owner._scopeFactory.CreateScope();
                    var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
                    job = await jobRepository.DequeueNextForProjectAsync(projectId, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    _slots.Release();
                    return;
                }
                catch (Exception ex)
                {
                    _slots.Release();
                    owner._logger.LogError(ex,
                        "Unhandled error while claiming the next job for project {ProjectId}; the next signal or poll tick will try again.",
                        projectId);
                    return;
                }

                if (job is null)
                {
                    _slots.Release();
                    return;
                }

                _ = owner.ProcessClaimedJobAsync(job, ct).ContinueWith(
                    _ =>
                    {
                        _slots.Release();
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
    }

    /// <summary>
    /// Runs one job already claimed (marked <see cref="JobStatus.Running"/>) by
    /// <see cref="IJobRepository.DequeueNextForProjectAsync"/> or <see cref="ForceRunAsync"/>. Never lets
    /// an exception escape -- this is invoked fire-and-forget, so an unhandled exception here would
    /// otherwise become an unobserved task exception instead of a logged, recoverable failure.
    /// </summary>
    private async Task ProcessClaimedJobAsync(Job job, CancellationToken ct)
    {
        using var execution = new JobExecution(ct);
        _executions[job.Id] = execution;
        try
        {
            await RunClaimedJobAsync(job, execution, ct).ConfigureAwait(false);
        }
        finally
        {
            // A forced run may already have registered its own execution under this id.
            _executions.TryRemove(KeyValuePair.Create(job.Id, execution));
        }
    }

    private async Task RunClaimedJobAsync(Job job, JobExecution execution, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();

            var handler = scope.ServiceProvider.GetServices<Handlers.IJobHandler>().FirstOrDefault(h => h.Type == job.Type);
            if (handler is null)
            {
                job.Status = JobStatus.Failed;
                job.LastError = $"No {nameof(Handlers.IJobHandler)} is registered for job type {job.Type}.";
                job.FinishedAt = DateTimeOffset.UtcNow;
                await jobRepository.UpdateAsync(job, ct).ConfigureAwait(false);
                _logger.LogError("No handler registered for job type {JobType} (job {JobId}); marking Failed.", job.Type, job.Id);
                await PublishJobCountsAsync(job.ProjectId, ct).ConfigureAwait(false);
                return;
            }

            try
            {
                await handler.HandleAsync(job, execution.Token).ConfigureAwait(false);
                if (execution.Superseded)
                {
                    return;
                }

                job.Status = JobStatus.Done;
                job.FinishedAt = DateTimeOffset.UtcNow;
                job.LastError = null;
                job.NextRetryAt = null;
                await jobRepository.UpdateAsync(job, ct).ConfigureAwait(false);
                await PublishJobCountsAsync(job.ProjectId, ct).ConfigureAwait(false);
            }
            catch (Exception) when (execution.Superseded)
            {
                // The forced run owns the row now; any write from here would clobber it.
                _logger.LogInformation("Job {JobId} ({JobType}) execution abandoned in favour of a forced run.", job.Id, job.Type);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host is shutting down mid-job, not a job failure: put it back exactly as it was
                // found so HydrateOnStartupAsync picks it up again next run, without burning an
                // attempt. Deliberately CancellationToken.None here - ct is already cancelled at this
                // point, and this compensating write must still land (otherwise the job is merely left
                // Running, which HydrateOnStartupAsync would still recover on the next start, just less
                // cleanly - see remarks). Same reasoning applies to the counts push right after it.
                job.Status = JobStatus.Pending;
                job.Attempts = Math.Max(0, job.Attempts - 1);
                job.StartedAt = null;
                await jobRepository.UpdateAsync(job, CancellationToken.None).ConfigureAwait(false);
                await PublishJobCountsAsync(job.ProjectId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await HandleFailureAsync(jobRepository, job, ex, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down before even getting a scope/handler resolved -- the row is whatever
            // DequeueNextAsync left it as (Running); HydrateOnStartupAsync recovers it next start.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error processing job {JobId} ({JobType}).", job.Id, job.Type);
        }
    }

    /// <summary>One in-process run of a claimed job; <see cref="Supersede"/> is how
    /// <see cref="ForceRunAsync"/> cancels it and stops it writing the job row.</summary>
    private sealed class JobExecution(CancellationToken lifetime) : IDisposable
    {
        private readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _superseded;

        public CancellationToken Token => _cts.Token;
        public bool Superseded => _superseded;
        public Task Completion => _completion.Task;

        public void Supersede()
        {
            _superseded = true;
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Finished between being looked up and being cancelled.
            }
        }

        public void Dispose()
        {
            _completion.TrySetResult();
            _cts.Dispose();
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
            await PublishJobCountsAsync(job.ProjectId, ct).ConfigureAwait(false);
            return;
        }

        // Exponential backoff: 2s, 4s, 8s, ... capped at 5 minutes so a mis-tuned attempt count
        // never leaves a job in limbo for an unreasonable time. DequeueNextAsync's own NextRetryAt
        // filter (picked up by the next poll tick, see this type's remarks) is what actually
        // resumes it -- no per-job timer here any more.
        var backoff = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, job.Attempts + 1)));
        job.Status = JobStatus.Pending;
        job.NextRetryAt = DateTimeOffset.UtcNow + backoff;
        await jobRepository.UpdateAsync(job, ct).ConfigureAwait(false);

        _logger.LogWarning(ex,
            "Job {JobId} ({JobType}) failed (attempt {Attempts}/{MaxAttempts}); retrying at {NextRetryAt:O}.",
            job.Id, job.Type, job.Attempts, maxAttempts, job.NextRetryAt);

        await PublishJobCountsAsync(job.ProjectId, ct).ConfigureAwait(false);
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
