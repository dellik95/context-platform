using Context.Domain.Enums;

namespace Context.Application.Jobs;

/// <summary>
/// Thrown by <see cref="IJobQueue.RequeueAsync"/> when asked to retry a job that isn't currently
/// <see cref="JobStatus.Failed"/> — retrying a Pending/Running/Done job doesn't make sense.
/// </summary>
public sealed class JobNotRetryableException(Guid jobId, JobStatus actualStatus)
    : Exception($"Job '{jobId}' is not retryable: expected status {JobStatus.Failed}, but it is {actualStatus}.")
{
    public Guid JobId { get; } = jobId;
    public JobStatus ActualStatus { get; } = actualStatus;
}
