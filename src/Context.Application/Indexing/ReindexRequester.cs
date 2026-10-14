using Context.Application.Jobs;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;

namespace Context.Application.Indexing;

/// <summary>The one way a project reindex is requested: as a <see cref="JobType.ReindexProject"/> job, so it
/// survives a restart and shows in the job queue. Shared by the dashboard trigger, the reindex scheduler and
/// the file watcher's recovery after a lost event.</summary>
public sealed class ReindexRequester(IJobQueue jobQueue, IJobRepository jobs)
{
    /// <param name="skipIfActive">Leave a reindex already queued or running alone. An explicit user request
    /// passes false: the new run cancels the running one and starts over.</param>
    /// <returns>The job id, or null when skipped.</returns>
    public async Task<Guid?> RequestAsync(Guid projectId, bool full, bool skipIfActive, CancellationToken ct)
    {
        if (skipIfActive && (await jobs.ListActiveByTypeAsync(JobType.ReindexProject, projectId, ct).ConfigureAwait(false)).Count > 0)
        {
            return null;
        }

        return await jobQueue.EnqueueAsync(
            JobType.ReindexProject, new ReindexProjectPayload(projectId, full), projectId, JobPriority.Session, ct).ConfigureAwait(false);
    }
}
