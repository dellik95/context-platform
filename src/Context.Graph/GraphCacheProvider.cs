using System.Collections.Concurrent;
using Context.Graph.Abstractions;

namespace Context.Graph;

/// <summary>
/// Singleton dictionary of <see cref="InMemoryGraphCache"/> instances keyed by ProjectId — the
/// "keyed/per-project singleton dictionary service" registered by <c>AddContextGraph</c>. Each
/// project's cache is built lazily on first access and kept warm for the life of the process;
/// <see cref="GraphStore"/> keeps warm caches in sync incrementally on every write.
/// </summary>
public sealed class GraphCacheProvider : IGraphCacheProvider
{
    private readonly IGraphPersistence _persistence;
    private readonly ConcurrentDictionary<Guid, Lazy<Task<InMemoryGraphCache>>> _caches = new();
    private readonly ConcurrentDictionary<Guid, InMemoryGraphCache> _loadedCaches = new();

    public GraphCacheProvider(IGraphPersistence persistence)
    {
        _persistence = persistence;
    }

    public async Task<InMemoryGraphCache> GetOrCreateAsync(Guid projectId, CancellationToken ct)
    {
        var lazy = _caches.GetOrAdd(
            projectId,
            id => new Lazy<Task<InMemoryGraphCache>>(() => CreateAndTrackAsync(id)));
        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch when (lazy.IsValueCreated && (lazy.Value.IsFaulted || lazy.Value.IsCanceled))
        {
            // Don't let a failed/cancelled build permanently poison the slot - next caller retries.
            // Caller-side WaitAsync(ct) cancellation does not evict an in-flight healthy load.
            _caches.TryRemove(new KeyValuePair<Guid, Lazy<Task<InMemoryGraphCache>>>(projectId, lazy));
            _loadedCaches.TryRemove(projectId, out _);
            throw;
        }
    }

    public bool TryGetLoaded(Guid projectId, out InMemoryGraphCache? cache) =>
        _loadedCaches.TryGetValue(projectId, out cache);

    public InMemoryGraphCache? FindCacheContaining(Guid nodeId)
    {
        foreach (var cache in _loadedCaches.Values)
        {
            if (cache.ContainsNode(nodeId))
                return cache;
        }

        return null;
    }

    public IReadOnlyCollection<InMemoryGraphCache> LoadedCaches => [.. _loadedCaches.Values];

    private async Task<InMemoryGraphCache> CreateAndTrackAsync(Guid projectId)
    {
        var cache = await InMemoryGraphCache.CreateAsync(projectId, _persistence, CancellationToken.None).ConfigureAwait(false);
        _loadedCaches[projectId] = cache;
        return cache;
    }
}
