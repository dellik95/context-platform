using System.Threading.Channels;

namespace Context.Application.Jobs;

/// <summary>
/// Default, no-external-dependency <see cref="IJobSignal"/>: a single-process broadcast over an
/// unbounded <see cref="Channel{T}"/>. Correct for the common single-instance deployment (today's
/// only supported one) but does not reach other processes -- <c>Context.Dashboard.Server</c>
/// registers a Redis-backed implementation instead when multi-instance operation is configured (see
/// its own DI wiring), falling back to this one otherwise.
/// </summary>
public sealed class InMemoryJobSignal : IJobSignal
{
    // Capacity 1, DropOldest: a signal only ever means "go check again" -- once one is pending,
    // more of the same before a listener gets to it add no information, so collapsing them into
    // one avoids the queue growing unbounded under a burst of EnqueueAsync calls.
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = false, SingleWriter = false });

    public Task PublishAsync(CancellationToken ct)
    {
        _channel.Writer.TryWrite(true);
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<bool> ListenAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
