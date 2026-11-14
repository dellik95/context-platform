using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Infrastructure.Abstractions;

public interface IJobRepository
{
    /// <summary>The job, or null if it doesn't exist. When <paramref name="projectId"/> is provided,
    /// the job is only returned if it belongs to that project or is host-wide (<see cref="Job.ProjectId"/>
    /// is null) — otherwise it is treated as not found. Omitting <paramref name="projectId"/> preserves
    /// today's unfiltered behavior.</summary>
    Task<Job?> GetByIdAsync(Guid id, CancellationToken ct, Guid? projectId = null);
    Task<Job> EnqueueAsync(Job job, CancellationToken ct);
    Task UpdateAsync(Job job, CancellationToken ct);

    /// <summary>Marks every currently Running job back to Pending on dispatcher startup (clean or crash recovery).</summary>
    Task<int> ResetStaleRunningAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>Atomically claims and returns the highest-priority runnable pending job (oldest first
    /// within the same priority), marking it Running, or null if none are runnable right now.</summary>
    Task<Job?> DequeueNextAsync(CancellationToken ct);

    /// <summary><see cref="DequeueNextAsync"/> restricted to jobs whose <see cref="Job.ProjectId"/>
    /// equals <paramref name="projectId"/> exactly — null claims only host-wide jobs, not "any" — and to the
    /// job types <paramref name="filter"/> allows. Backs the dispatcher's per-project lanes.</summary>
    Task<Job?> DequeueNextForProjectAsync(Guid? projectId, JobLaneFilter filter, CancellationToken ct);

    /// <summary>Claims one job regardless of priority, retry backoff or lane capacity, as a fresh
    /// first attempt — only if it is still in <paramref name="expectedStatus"/>, else null.</summary>
    Task<Job?> ForceClaimAsync(Guid id, JobStatus expectedStatus, CancellationToken ct);

    /// <summary>Distinct <see cref="Job.ProjectId"/> values (null = host-wide) that have at least
    /// one Pending job, including ones still waiting out a retry backoff.</summary>
    Task<IReadOnlyList<Guid?>> ListProjectsWithPendingJobsAsync(CancellationToken ct);

    /// <summary>When <paramref name="projectId"/> is null, returns jobs for every project (plus
    /// host-wide maintenance jobs) — the pre-filtering, host-wide behavior.</summary>
    Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, Guid? projectId, CancellationToken ct);

    /// <summary>Breakdown of how many jobs currently sit in each <see cref="JobStatus"/>, scoped to
    /// <paramref name="projectId"/> (a host-wide maintenance job with no <see cref="Job.ProjectId"/>
    /// is excluded, same as <see cref="ListByStatusAsync"/>'s exact-match behavior with a non-null
    /// <paramref name="projectId"/>). Backs both <c>GET /api/system/status</c>'s four jobsXxx fields
    /// and <c>JobDispatcher</c>'s SignalR push of the same figures, so both stay computed exactly
    /// one way.</summary>
    Task<JobStatusCounts> CountByStatusAsync(Guid projectId, CancellationToken ct);

    /// <summary>How many jobs of each <see cref="JobType"/> sit in <paramref name="status"/> for the project.</summary>
    Task<IReadOnlyList<JobTypeCount>> CountByTypeAsync(JobStatus status, Guid projectId, CancellationToken ct);

    /// <summary>
    /// Bulk-deletes every job row in <paramref name="status"/> (optionally scoped to one project and
    /// to one <paramref name="type"/>, and never touching <paramref name="excludeTypes"/>), returning
    /// how many were removed. Backs the System tab's "clean up job queue" action — a
    /// long-running host accumulates one permanent <see cref="Job"/> row per completed EmbedChunk
    /// job with nothing left to act on, and this is the only way to shrink that table back down.
    /// </summary>
    Task<int> DeleteByStatusAsync(JobStatus status, Guid? projectId, JobType? type, IReadOnlyCollection<JobType>? excludeTypes, CancellationToken ct);

    /// <summary>
    /// Deletes terminal (<see cref="JobStatus.Done"/> / <see cref="JobStatus.Failed"/>) jobs that
    /// finished before <paramref name="cutoff"/> and match <paramref name="filter"/>, host-wide,
    /// returning how many rows went. The
    /// manual <see cref="DeleteByStatusAsync"/> cleanup exists but nothing calls it on its own, so
    /// this is what keeps the table from growing by one permanent row per embedded chunk forever
    /// on a host nobody clicks buttons on.
    /// </summary>
    Task<int> DeleteFinishedOlderThanAsync(DateTimeOffset cutoff, JobRetentionFilter filter, CancellationToken ct);

    /// <summary>Deletes every job of <paramref name="projectId"/> that is not running (used when the project is deleted),
    /// returning how many went.</summary>
    Task<int> DeleteByProjectAsync(Guid projectId, CancellationToken ct);

    /// <summary>The project's Pending or Running jobs of <paramref name="type"/>, without their payloads.</summary>
    Task<IReadOnlyList<ActiveJob>> ListActiveByTypeAsync(JobType type, Guid projectId, CancellationToken ct);

    /// <summary>
    /// When a job of <paramref name="type"/> was last enqueued host-wide, or null if none ever was.
    /// Lets the daily maintenance scheduler derive its own cadence from the queue instead of from
    /// process uptime — a host restarted more often than the interval would otherwise never reach
    /// its first tick and silently skip backups forever.
    /// </summary>
    Task<DateTimeOffset?> GetLastEnqueuedAtAsync(JobType type, CancellationToken ct);
}

/// <summary>Which job types a dispatcher lane may claim. Unset members match everything.</summary>
public sealed record JobLaneFilter(IReadOnlyCollection<JobType>? OnlyTypes = null, IReadOnlyCollection<JobType>? ExceptTypes = null)
{
    public static readonly JobLaneFilter All = new();
}

/// <summary>Narrows a retention sweep. Unset members match everything.</summary>
public sealed record JobRetentionFilter(JobType? OnlyType = null, JobType? ExceptType = null, JobStatus? OnlyStatus = null)
{
    public static readonly JobRetentionFilter All = new();
}

public sealed record ActiveJob(Guid Id, DateTimeOffset CreatedAt);

public sealed record JobStatusCounts(int Pending, int Running, int Failed, int Done);

public sealed record JobTypeCount(JobType Type, int Count);
