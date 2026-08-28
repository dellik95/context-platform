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
}
