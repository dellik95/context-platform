namespace Context.Graph.Abstractions;

/// <summary>
/// Per-project registry of <see cref="Context.Graph.InMemoryGraphCache"/> instances — a single
/// singleton service that behaves as a keyed (by ProjectId) dictionary of caches, each built
/// lazily on first access from <see cref="IGraphPersistence"/> and then kept warm and updated
/// incrementally by <see cref="IGraphStore"/>.
/// </summary>
public interface IGraphCacheProvider
{
    /// <summary>Gets the cache for <paramref name="projectId"/>, building it from persistence on first access.</summary>
    Task<Context.Graph.InMemoryGraphCache> GetOrCreateAsync(Guid projectId, CancellationToken ct);

    /// <summary>Non-blocking peek: true only if the cache for this project is already warm.</summary>
    bool TryGetLoaded(Guid projectId, out Context.Graph.InMemoryGraphCache? cache);

    /// <summary>
    /// Scans already-loaded caches for the one containing <paramref name="nodeId"/>.
    /// <see cref="IGraphTraversal"/>'s contract methods take no ProjectId, so this is how a node
    /// is resolved to its project; a project whose cache was never warmed will not be found here
    /// (see follow-up note in Context.Graph's implementation report).
    /// </summary>
    Context.Graph.InMemoryGraphCache? FindCacheContaining(Guid nodeId);

    IReadOnlyCollection<Context.Graph.InMemoryGraphCache> LoadedCaches { get; }
}
