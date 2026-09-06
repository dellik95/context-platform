namespace Context.Application.Jobs;

/// <summary>
/// Cross-instance "there is work to check for" wake-up used by <see cref="JobDispatcher"/>. Carries
/// no payload -- a signal never says which job is ready, only that <em>something</em> might be, and
/// every listener responds by atomically claiming from the durable job table itself
/// (<see cref="Context.Infrastructure.Abstractions.IJobRepository.DequeueNextAsync"/>), never from
/// the signal. That split is what makes this safe to fan out to more than one process: two
/// <c>Context.Dashboard.Server</c> instances sharing one database can both listen to the same
/// signal and both attempt to claim work, but the database's own atomic claim (a transaction that
/// flips <c>Pending</c> to <c>Running</c>) is what actually decides which one gets each job -- the
/// signal is just what makes them look promptly instead of only on the next poll tick.
/// </summary>
public interface IJobSignal
{
    /// <summary>Wakes every current listener (in this process, and in every other process sharing
    /// the same backing transport). Fire-and-forget in spirit -- a dropped notification only costs
    /// a listener the next fallback poll tick, never correctness.</summary>
    Task PublishAsync(CancellationToken ct);

    /// <summary>Yields once per notification received, until <paramref name="ct"/> is cancelled.
    /// The yielded value carries no information; it exists only to end the wait.</summary>
    IAsyncEnumerable<bool> ListenAsync(CancellationToken ct);
}
