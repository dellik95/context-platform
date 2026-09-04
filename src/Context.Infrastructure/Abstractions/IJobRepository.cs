using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Infrastructure.Abstractions;

public interface IJobRepository
{
    Task<Job?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Job> EnqueueAsync(Job job, CancellationToken ct);
    Task UpdateAsync(Job job, CancellationToken ct);

    /// <summary>Atomically claims and returns the highest-priority runnable pending job (oldest first
    /// within the same priority), marking it Running, or null if none are runnable right now.</summary>
    Task<Job?> DequeueNextAsync(CancellationToken ct);

    Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken ct);

    /// <summary>Host-wide (not project-scoped -- jobs aren't queried per project anywhere else
    /// either) breakdown of how many jobs currently sit in each <see cref="JobStatus"/>. Backs
    /// both <c>GET /api/system/status</c>'s four jobsXxx fields and <c>JobDispatcher</c>'s
    /// SignalR push of the same figures, so both stay computed exactly one way.</summary>
    Task<JobStatusCounts> CountByStatusAsync(CancellationToken ct);
}

public sealed record JobStatusCounts(int Pending, int Running, int Failed, int Done);
