using System.Collections.Concurrent;
using Context.Application.Notifications;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Context.Application.Jobs;

/// <summary>
/// Durable-log-plus-signal <see cref="IJobQueue"/> and <see cref="IHostedService"/> orchestrator.
/// Coordinates per-project lanes (<see cref="JobLane"/>), signals/polling loops, and delegates
/// job execution/retry semantics to <see cref="JobExecutionRunner"/>.
/// </summary>
public sealed class JobDispatcher : IJobQueue, IJobDispatcherHealth, IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<JobDispatcher> _logger;
    private readonly IJobSignal _jobSignal;
    private readonly TimeProvider _timeProvider;
    private readonly JobWorkerPool _workerPool;
    private readonly JobWorkerPool _lightPool;
    private readonly JobExecutionRunner _runner;

    private readonly ConcurrentDictionary<Guid, JobLane> _lanes = new();
    private readonly ConcurrentDictionary<Guid, JobLane> _lightLanes = new();
    private readonly ConcurrentDictionary<Task, byte> _forceRunTasks = new();

    private CancellationTokenSource? _lifetimeCts;
    private Task? _processingTask;
    private int _stopped;
    private int _disposed;

    public bool IsRunning =>
        Volatile.Read(ref _disposed) == 0
        && Volatile.Read(ref _stopped) == 0
        && _lifetimeCts is not null
        && _processingTask is { IsCompleted: false };

    public JobDispatcher(
        IServiceScopeFactory scopeFactory,
        ILogger<JobDispatcher>? logger = null,
        IJobCountsNotifier? jobCountsNotifier = null,
        IJobSignal? jobSignal = null,
        IOptions<JobDispatcherOptions>? options = null,
        TimeProvider? timeProvider = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger ?? NullLogger<JobDispatcher>.Instance;
        _jobSignal = jobSignal ?? new InMemoryJobSignal();
        _timeProvider = timeProvider ?? TimeProvider.System;
        var dispatcherOptions = options?.Value ?? new JobDispatcherOptions();
        _workerPool = new JobWorkerPool(Math.Max(1, dispatcherOptions.WorkersPerProject), Math.Max(0, dispatcherOptions.SharedWorkers));
        _lightPool = new JobWorkerPool(Math.Max(1, dispatcherOptions.LightWorkersPerProject), 0);
        _runner = new JobExecutionRunner(scopeFactory, jobCountsNotifier ?? NullJobCountsNotifier.Instance, _logger, _timeProvider);
    }

    public async Task<Guid> EnqueueAsync(JobType type, object payload, Guid? projectId = null, int priority = 0, CancellationToken ct = default)
    {
        var job = JobFactory.Create(type, payload, projectId, priority);

        using (var scope = _scopeFactory.CreateScope())
        {
            var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            job = await jobRepository.EnqueueAsync(job, ct).ConfigureAwait(false);
        }

        await WakeAsync(projectId, ct).ConfigureAwait(false);
        return job.Id;
    }

    public async Task WakeAsync(Guid? projectId, CancellationToken ct = default)
    {
        KickLane(projectId);
        await _jobSignal.PublishAsync(ct).ConfigureAwait(false);
        await _runner.PublishJobCountsAsync(projectId, ct).ConfigureAwait(false);
    }

    public async Task RequeueAsync(Guid jobId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        var job = await jobRepository.GetByIdAsync(jobId, ct).ConfigureAwait(false)
            ?? throw new JobNotFoundException(jobId);

        if (job.Status != JobStatus.Failed)
        {
            throw new JobStatusConflictException(jobId, job.Status, "retried");
        }

        job.Status = JobStatus.Pending;
        job.Attempts = 0;
        job.LastError = null;
        job.NextRetryAt = null;
        job.StartedAt = null;
        job.FinishedAt = null;
        await jobRepository.UpdateAsync(job, ct).ConfigureAwait(false);

        _logger.LogInformation("Job {JobId} ({JobType}) manually requeued after permanent failure.", job.Id, job.Type);

        KickLane(job.ProjectId);
        await _jobSignal.PublishAsync(ct).ConfigureAwait(false);
        await _runner.PublishJobCountsAsync(job.ProjectId, ct).ConfigureAwait(false);
    }

    public async Task ForceRunAsync(Guid jobId, CancellationToken ct = default)
    {
        if (_lifetimeCts is not { } cts || Volatile.Read(ref _stopped) != 0)
        {
            throw new InvalidOperationException("The job dispatcher is not running.");
        }

        await _runner.SupersedeIfRunningAsync(jobId, ct).ConfigureAwait(false);

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

        var forceTask = _runner.ProcessClaimedJobAsync(claimed, cts.Token);
        _forceRunTasks[forceTask] = 0;
        _ = forceTask.ContinueWith(
            static (t, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _),
            _forceRunTasks,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        await _runner.PublishJobCountsAsync(claimed.ProjectId, ct).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetimeCts = new CancellationTokenSource();
        await HydrateOnStartupAsync(_lifetimeCts.Token).ConfigureAwait(false);
        _processingTask = Task.WhenAll(ListenLoopAsync(_lifetimeCts.Token), PollLoopAsync(_lifetimeCts.Token));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetimeCts is null || Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _lifetimeCts.Cancel();

        try
        {
            if (_processingTask is not null)
            {
                await _processingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var drainTasks = _lanes.Values
                .Concat(_lightLanes.Values)
                .Select(lane => lane.WaitForIdleAsync(cancellationToken))
                .Concat(_forceRunTasks.Keys.Select(t => t.WaitAsync(cancellationToken)));

            await Task.WhenAll(drainTasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Shutdown timeout or own lifetime cancellation.
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

    private async Task HydrateOnStartupAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();

        var reset = await jobRepository.ResetStaleRunningAsync(_timeProvider.GetUtcNow(), ct).ConfigureAwait(false);
        if (reset > 0)
        {
            _logger.LogWarning(
                "{Count} job(s) were left Running at startup — a previous process must have crashed mid-job; reset to Pending.",
                reset);
        }
    }

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
            _logger.LogError(ex, "Job signal listener stopped unexpectedly; falling back to polling every {PollInterval}.", PollInterval);
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
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
            return;
        }

        var key = projectId ?? Guid.Empty;
        var state = (Dispatcher: this, ProjectId: projectId);
        _lanes.GetOrAdd(key, static (_, s) => s.Dispatcher.CreateLane(s.ProjectId, light: false, s.Dispatcher._workerPool), state).Kick(cts.Token);
        _lightLanes.GetOrAdd(key, static (_, s) => s.Dispatcher.CreateLane(s.ProjectId, light: true, s.Dispatcher._lightPool), state).Kick(cts.Token);
    }

    private JobLane CreateLane(Guid? projectId, bool light, JobWorkerPool pool) =>
        new(projectId, light, pool, _scopeFactory, _runner.ProcessClaimedJobAsync, KickWaitingLanes, _logger);

    private void KickWaitingLanes(IReadOnlyList<Guid> laneKeys, bool light, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        var map = light ? _lightLanes : _lanes;
        foreach (var key in laneKeys)
        {
            if (map.TryGetValue(key, out var lane))
            {
                lane.Kick(ct);
            }
        }
    }
}
