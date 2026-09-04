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

    /// <summary>Atomically claims and returns the highest-priority runnable pending job (oldest first
    /// within the same priority), marking it Running, or null if none are runnable right now.</summary>
    Task<Job?> DequeueNextAsync(CancellationToken ct);

    /// <summary>When <paramref name="projectId"/> is null, returns jobs for every project (plus
    /// host-wide maintenance jobs) — the pre-filtering, host-wide behavior.</summary>
    Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, Guid? projectId, CancellationToken ct);

    /// <summary>Host-wide (not project-scoped -- jobs aren't queried per project anywhere else
    /// either) breakdown of how many jobs currently sit in each <see cref="JobStatus"/>. Backs
    /// both <c>GET /api/system/status</c>'s four jobsXxx fields and <c>JobDispatcher</c>'s
    /// SignalR push of the same figures, so both stay computed exactly one way.</summary>
    Task<JobStatusCounts> CountByStatusAsync(CancellationToken ct);
}

public sealed record JobStatusCounts(int Pending, int Running, int Failed, int Done);
