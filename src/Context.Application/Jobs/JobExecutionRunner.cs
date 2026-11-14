using System.Collections.Concurrent;
using Context.Application.Notifications;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Context.Application.Jobs;

/// <summary>
/// Executes claimed jobs inside isolated DI scopes, handles retries/deferrals/permanent failures,
/// supports superseding in-flight executions, and publishes updated project job counts.
/// </summary>
internal sealed class JobExecutionRunner(
    IServiceScopeFactory scopeFactory,
    IJobCountsNotifier jobCountsNotifier,
    ILogger logger,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan SupersedeGrace = TimeSpan.FromSeconds(10);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, JobExecution> _executions = new();

    public async Task SupersedeIfRunningAsync(Guid jobId, CancellationToken ct)
    {
        if (!_executions.TryGetValue(jobId, out var inFlight))
        {
            return;
        }

        inFlight.Supersede();
        try
        {
            await inFlight.Completion.WaitAsync(SupersedeGrace, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Job {JobId} did not stop within {Grace} of being cancelled; abandoning it.", jobId, SupersedeGrace);
        }
    }

    public async Task ProcessClaimedJobAsync(Job job, CancellationToken ct)
    {
        using var execution = new JobExecution(ct);
        _executions[job.Id] = execution;
        try
        {
            await RunClaimedJobAsync(job, execution, ct).ConfigureAwait(false);
        }
        finally
        {
            _executions.TryRemove(KeyValuePair.Create(job.Id, execution));
        }
    }

    public async Task PublishJobCountsAsync(Guid? projectId, CancellationToken ct)
    {
        if (projectId is not { } id)
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();
            var counts = await jobRepository.CountByStatusAsync(id, ct).ConfigureAwait(false);
            await jobCountsNotifier.NotifyAsync(
                id, new JobCounts(counts.Pending, counts.Running, counts.Failed, counts.Done), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish job counts after a status change.");
        }
    }

    private async Task RunClaimedJobAsync(Job job, JobExecution execution, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobRepository = scope.ServiceProvider.GetRequiredService<IJobRepository>();

            var handler = scope.ServiceProvider.GetServices<Handlers.IJobHandler>().FirstOrDefault(h => h.Type == job.Type);
            if (handler is null)
            {
                await MarkMissingHandlerFailedAsync(jobRepository, job, ct).ConfigureAwait(false);
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
                job.FinishedAt = _timeProvider.GetUtcNow();
                job.LastError = null;
                job.NextRetryAt = null;
                await UpdateAndNotifyAsync(jobRepository, job, ct).ConfigureAwait(false);
            }
            catch (Exception) when (execution.Superseded)
            {
                logger.LogInformation("Job {JobId} ({JobType}) execution abandoned in favour of a forced run.", job.Id, job.Type);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                job.Status = JobStatus.Pending;
                job.Attempts = Math.Max(0, job.Attempts - 1);
                job.StartedAt = null;
                await UpdateAndNotifyAsync(jobRepository, job, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await HandleFailureAsync(jobRepository, job, ex, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down before resolving a scope/handler; startup hydration resets Running jobs.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error processing job {JobId} ({JobType}).", job.Id, job.Type);
        }
    }

    private async Task MarkMissingHandlerFailedAsync(IJobRepository jobRepository, Job job, CancellationToken ct)
    {
        job.Status = JobStatus.Failed;
        job.LastError = $"No {nameof(Handlers.IJobHandler)} is registered for job type {job.Type}.";
        job.FinishedAt = _timeProvider.GetUtcNow();
        await UpdateAndNotifyAsync(jobRepository, job, ct).ConfigureAwait(false);
        logger.LogError("No handler registered for job type {JobType} (job {JobId}); marking Failed.", job.Type, job.Id);
    }

    private async Task HandleFailureAsync(IJobRepository jobRepository, Job job, Exception ex, CancellationToken ct)
    {
        if (FindDeferral(ex) is var (retryAt, deferralMessage))
        {
            job.Status = JobStatus.Pending;
            job.Attempts = Math.Max(0, job.Attempts - 1);
            job.StartedAt = null;
            job.NextRetryAt = retryAt;
            job.LastError = deferralMessage;
            await UpdateAndNotifyAsync(jobRepository, job, ct).ConfigureAwait(false);

            logger.LogWarning(
                "Job {JobId} ({JobType}) was deferred ({Reason}); retrying at {NextRetryAt:O} without spending an attempt.",
                job.Id, job.Type, deferralMessage, job.NextRetryAt);
            return;
        }

        var budget = JobRetryPolicy.For(job.Type);
        var maxAttempts = budget.MaxAttempts;
        var message = ex.ToString();
        job.LastError = message.Length > 4000 ? message[..4000] : message;

        if (job.Attempts >= maxAttempts)
        {
            job.Status = JobStatus.Failed;
            job.FinishedAt = _timeProvider.GetUtcNow();
            job.NextRetryAt = null;
            await UpdateAndNotifyAsync(jobRepository, job, ct).ConfigureAwait(false);

            logger.LogError(ex,
                "Job {JobId} ({JobType}) failed permanently after {Attempts}/{MaxAttempts} attempts.",
                job.Id, job.Type, job.Attempts, maxAttempts);
            return;
        }

        job.Status = JobStatus.Pending;
        job.NextRetryAt = _timeProvider.GetUtcNow() + budget.BackoffAfter(job.Attempts);
        await UpdateAndNotifyAsync(jobRepository, job, ct).ConfigureAwait(false);

        logger.LogWarning(ex,
            "Job {JobId} ({JobType}) failed (attempt {Attempts}/{MaxAttempts}); retrying at {NextRetryAt:O}.",
            job.Id, job.Type, job.Attempts, maxAttempts, job.NextRetryAt);
    }

    private async Task UpdateAndNotifyAsync(IJobRepository jobRepository, Job job, CancellationToken ct)
    {
        await jobRepository.UpdateAsync(job, ct).ConfigureAwait(false);
        await PublishJobCountsAsync(job.ProjectId, ct).ConfigureAwait(false);
    }

    private static (DateTimeOffset RetryAt, string Message)? FindDeferral(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case ProviderRateLimitedException rateLimited:
                    return (rateLimited.RetryAt, rateLimited.Message);
                case JobDeferredException deferred:
                    return (deferred.RetryAt, deferred.Message);
            }
        }

        return null;
    }
}
