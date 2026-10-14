namespace Context.Application.Jobs;

/// <summary>
/// Thrown by a handler that cannot make progress yet but has nothing wrong with it (for example it
/// must wait for earlier jobs). The dispatcher puts the job back to Pending at <see cref="RetryAt"/>
/// without spending an attempt, the same way it treats a provider rate limit.
/// </summary>
public sealed class JobDeferredException(DateTimeOffset retryAt, string message) : Exception(message)
{
    public DateTimeOffset RetryAt { get; } = retryAt;
}
