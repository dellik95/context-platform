using Context.Domain.Enums;

namespace Context.Application.Jobs;

/// <summary>See CONTRACTS.md §10. Implemented by <see cref="JobDispatcher"/>.</summary>
public interface IJobQueue
{
    /// <summary><paramref name="projectId"/> is null for host-wide maintenance job types
    /// (<see cref="JobType.DecayMemory"/>, <see cref="JobType.BackupDb"/>) that aren't scoped to
    /// any single project; every other call site should pass the owning project's id so the
    /// Dashboard's Jobs view can filter by currently-selected project.</summary>
    Task<Guid> EnqueueAsync(JobType type, object payload, Guid? projectId = null, int priority = 0, CancellationToken ct = default);

    /// <summary>
    /// Tells the dispatcher that jobs for <paramref name="projectId"/> were inserted directly through
    /// <see cref="Context.Infrastructure.Abstractions.IJobRepository"/> (inside a caller's database
    /// transaction, where <see cref="EnqueueAsync"/> would open a second connection and wait on that
    /// transaction's write lock) and are now committed and ready to claim.
    /// </summary>
    Task WakeAsync(Guid? projectId, CancellationToken ct = default);

    /// <summary>
    /// Manually re-triggers a job that has already exhausted its automatic retries and is
    /// permanently <see cref="Context.Domain.Enums.JobStatus.Failed"/> (the System tab's "retry"
    /// button) — resets it to a fresh <see cref="Context.Domain.Enums.JobStatus.Pending"/> attempt
    /// and hands it straight back to the live worker queue, rather than just flipping its DB row
    /// and leaving it stranded until the next process restart's hydration pass.
    /// </summary>
    /// <exception cref="JobNotFoundException">No job with <paramref name="jobId"/> exists.</exception>
    /// <exception cref="JobStatusConflictException">The job exists but isn't currently Failed.</exception>
    Task RequeueAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>
    /// Runs a Pending, Running or Failed job immediately as a fresh attempt, bypassing retry backoff
    /// and its project lane's slot limit. If this process is executing it right now (e.g. stuck in a
    /// hung LLM call), that execution is cancelled and abandoned first — it no longer writes to the row.
    /// </summary>
    /// <exception cref="JobNotFoundException">No job with <paramref name="jobId"/> exists.</exception>
    /// <exception cref="JobStatusConflictException">The job is Done, or changed status mid-claim.</exception>
    Task ForceRunAsync(Guid jobId, CancellationToken ct = default);
}
