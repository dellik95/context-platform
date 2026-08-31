using Context.Domain.Enums;

namespace Context.Application.Jobs;

/// <summary>See CONTRACTS.md §10. Implemented by <see cref="JobDispatcher"/>.</summary>
public interface IJobQueue
{
    Task<Guid> EnqueueAsync(JobType type, object payload, int priority = 0, CancellationToken ct = default);

    /// <summary>
    /// Manually re-triggers a job that has already exhausted its automatic retries and is
    /// permanently <see cref="Context.Domain.Enums.JobStatus.Failed"/> (the System tab's "retry"
    /// button) — resets it to a fresh <see cref="Context.Domain.Enums.JobStatus.Pending"/> attempt
    /// and hands it straight back to the live worker queue, rather than just flipping its DB row
    /// and leaving it stranded until the next process restart's hydration pass.
    /// </summary>
    /// <exception cref="JobNotFoundException">No job with <paramref name="jobId"/> exists.</exception>
    /// <exception cref="JobNotRetryableException">The job exists but isn't currently Failed.</exception>
    Task RequeueAsync(Guid jobId, CancellationToken ct = default);
}
