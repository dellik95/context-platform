namespace Context.Providers;

/// <summary>
/// The provider refused the call on quota grounds (HTTP 429). Not a fault in the caller's work:
/// JobDispatcher parks the job until <see cref="RetryAt"/> without spending one of its attempts.
/// </summary>
public sealed class ProviderRateLimitedException(string message, DateTimeOffset retryAt, Exception? innerException = null)
    : Exception(message, innerException)
{
    public DateTimeOffset RetryAt { get; } = retryAt;
}
