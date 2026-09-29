using Context.Domain.Enums;

namespace Context.Application.Jobs;

/// <summary>The job exists but its current status doesn't allow the requested action.</summary>
public sealed class JobStatusConflictException(Guid jobId, JobStatus actualStatus, string action)
    : Exception($"Job '{jobId}' cannot be {action}: it is currently {actualStatus}.")
{
    public Guid JobId { get; } = jobId;
    public JobStatus ActualStatus { get; } = actualStatus;
}
