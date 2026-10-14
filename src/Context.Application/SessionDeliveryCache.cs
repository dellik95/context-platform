using System.Collections.Concurrent;

namespace Context.Application;

/// <summary>
/// Session-scoped "already delivered" tracker for <see cref="ContextEngine.SearchContextAsync"/>,
/// per the task brief §3: when the same node recurs for the same session within a TTL window, the
/// caller (an LLM agent with a limited context window) doesn't need the full content again — a
/// short back-reference is enough. Registered as a singleton so it survives across the
/// per-request scopes that host <see cref="ContextEngine"/> (which depends on Scoped
/// EF-Core-backed repositories and therefore cannot itself be a singleton).
/// </summary>
/// <remarks>
/// Concrete shape: a two-level <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed first by
/// session id then by node id, storing the UTC instant the node was last delivered in full. Pure
/// in-memory/process-local bookkeeping — a restart clears it, which is fine (worst case: a node
/// is "delivered" again in full, never a correctness problem). Entries older than
/// <see cref="Ttl"/> are swept lazily on access rather than via a background timer, which is
/// enough at the expected scale (a handful of active sessions, each touching at most a few
/// thousand nodes) — see follow-up note in the implementation report if this ever needs to run
/// against a great many long-lived concurrent sessions.
/// </remarks>
public sealed class SessionDeliveryCache
{
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(20);

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, DateTimeOffset>> _bySession = new();

    /// <summary>
    /// If <paramref name="nodeId"/> was already delivered to <paramref name="sessionKey"/> within
    /// the TTL, returns the prior delivery instant and leaves state unchanged. Otherwise records
    /// <paramref name="nodeId"/> as delivered now and returns null (meaning: send full content).
    /// </summary>
    public DateTimeOffset? TryGetRecentDelivery(string sessionKey, Guid nodeId)
    {
        var now = DateTimeOffset.UtcNow;
        var nodes = _bySession.GetOrAdd(sessionKey, static _ => new ConcurrentDictionary<Guid, DateTimeOffset>());

        // Lazy sweep: drop stale entries for this session so the dictionary doesn't grow forever
        // across a very long session that keeps touching new nodes.
        foreach (var (existingNodeId, deliveredAt) in nodes)
        {
            if (now - deliveredAt > Ttl)
            {
                nodes.TryRemove(existingNodeId, out _);
            }
        }

        if (nodes.TryGetValue(nodeId, out var previous) && now - previous <= Ttl)
        {
            return previous;
        }

        nodes[nodeId] = now;
        return null;
    }
}
