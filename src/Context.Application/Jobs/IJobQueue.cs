using Context.Domain.Enums;

namespace Context.Application.Jobs;

/// <summary>See CONTRACTS.md §10. Implemented by <see cref="JobDispatcher"/>.</summary>
public interface IJobQueue
{
    Task<Guid> EnqueueAsync(JobType type, object payload, int priority = 0, CancellationToken ct = default);
}
