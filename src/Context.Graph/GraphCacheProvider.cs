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
    private readonly ConcurrentDictionary<Guid, Task<InMemoryGraphCache>> _caches = new();

    public GraphCacheProvider(IGraphPersistence persistence)
    {
        _persistence = persistence;
    }

    public async Task<InMemoryGraphCache> GetOrCreateAsync(Guid projectId, CancellationToken ct)
    {
        var task = _caches.GetOrAdd(projectId, id => InMemoryGraphCache.CreateAsync(id, _persistence, ct));
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            // Don't let a failed/cancelled build permanently poison the slot - next caller retries.
            _caches.TryRemove(new KeyValuePair<Guid, Task<InMemoryGraphCache>>(projectId, task));
            throw;
        }
    }

    public bool TryGetLoaded(Guid projectId, out InMemoryGraphCache? cache)
    {
        if (_caches.TryGetValue(projectId, out var task) && task.IsCompletedSuccessfully)
        {
            cache = task.Result;
            return true;
        }

        cache = null;
        return false;
    }

    public InMemoryGraphCache? FindCacheContaining(Guid nodeId)
    {
        foreach (var task in _caches.Values)
        {
            if (task.IsCompletedSuccessfully && task.Result.ContainsNode(nodeId))
                return task.Result;
        }

        return null;
    }

    public IReadOnlyCollection<InMemoryGraphCache> LoadedCaches =>
        _caches.Values.Where(t => t.IsCompletedSuccessfully).Select(t => t.Result).ToArray();
}
