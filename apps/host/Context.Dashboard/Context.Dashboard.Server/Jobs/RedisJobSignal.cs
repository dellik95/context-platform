using Context.Application.Jobs;
using StackExchange.Redis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Context.Dashboard.Server.Jobs;

/// <summary>
/// Overrides Context.Application's <see cref="InMemoryJobSignal"/> default (see Program.cs
/// registration order) when <c>CONTEXT_PLATFORM_REDIS</c> is configured: publishes/subscribes on
/// one Redis pub/sub channel so every <c>Context.Dashboard.Server</c> instance pointed at the same
/// Redis (and the same database file — Redis coordinates *when* to look, not *what* to run) reacts
/// to the same "go check for work" wake-ups. As with every <see cref="IJobSignal"/> implementation,
/// the message payload itself carries no information — <see cref="JobDispatcher"/> always re-claims
/// via <see cref="Context.Infrastructure.Abstractions.IJobRepository.DequeueNextAsync"/>, so a
/// message this process's own publish loops back to itself (Redis pub/sub always echoes to the
/// publisher too, if subscribed) is harmless, just a slightly redundant claim attempt.
/// </summary>
/// <remarks>
/// Does not own or dispose <see cref="IConnectionMultiplexer"/> — it's injected as a separately
/// DI-registered singleton (see Program.cs), and that registration is what owns its lifetime.
/// </remarks>
public sealed class RedisJobSignal : IJobSignal
{
    private const string ChannelName = "context-platform:jobs:ready";

    private readonly ISubscriber _subscriber;

    public RedisJobSignal(IConnectionMultiplexer redis)
    {
        _subscriber = redis.GetSubscriber();
    }

    public async Task PublishAsync(CancellationToken ct)
    {
        try
        {
            await _subscriber.PublishAsync(RedisChannel.Literal(ChannelName), RedisValue.EmptyString).ConfigureAwait(false);
        }
        catch (RedisException)
        {
            // Fire-and-forget in spirit (see IJobSignal's remarks): a publish Redis couldn't
            // deliver right now only costs listeners the next poll-timer fallback, never
            // correctness, so it must never fail the caller's enqueue/retry request over it.
        }
    }

    /// <summary>
    /// Bridges StackExchange.Redis's callback-based subscription to <see cref="IAsyncEnumerable{T}"/>
    /// via a small relay channel — <see cref="ISubscriber.Subscribe(RedisChannel, Action{RedisChannel,RedisValue},CommandFlags)"/>
    /// has no async-stream form of its own. Same drop-instead-of-queue collapsing as
    /// <see cref="InMemoryJobSignal"/>: a burst of Redis messages before this listener catches up
    /// still only means "go check again" once.
    /// </summary>
    public async IAsyncEnumerable<bool> ListenAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var relay = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = false });

        var channel = await _subscriber.SubscribeAsync(RedisChannel.Literal(ChannelName)).ConfigureAwait(false);
        channel.OnMessage(_ =>
        {
            relay.Writer.TryWrite(true);
            return Task.CompletedTask;
        });

        try
        {
            await foreach (var item in relay.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            await channel.UnsubscribeAsync().ConfigureAwait(false);
        }
    }
}
