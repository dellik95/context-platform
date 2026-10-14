using Context.Application.Jobs;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;

namespace Context.Application.Indexing;

/// <summary>Queues a <see cref="JobType.RepairVectors"/> pass for a project unless one is already queued or running.</summary>
public sealed class VectorRepairRequester(IJobQueue jobQueue, IJobRepository jobs)
{
    public async Task<Guid?> RequestAsync(Guid projectId, CancellationToken ct)
    {
        if ((await jobs.ListActiveByTypeAsync(JobType.RepairVectors, projectId, ct).ConfigureAwait(false)).Count > 0)
        {
            return null;
        }

        return await jobQueue.EnqueueAsync(
            JobType.RepairVectors, new RepairVectorsPayload(projectId), projectId, JobPriority.Background, ct).ConfigureAwait(false);
    }
}
