namespace Context.Application.Jobs;

/// <summary>Thrown by <see cref="IJobQueue.RequeueAsync"/> when no job with the given id exists.</summary>
public sealed class JobNotFoundException(Guid jobId)
    : Exception($"No job with id '{jobId}' exists.")
{
    public Guid JobId { get; } = jobId;
}
