namespace Context.Application.Abstractions;

/// <summary>
/// Nearest-neighbor vector search over memory embeddings, backed by sqlite-vec -- the same pattern
/// <see cref="IChunkVectorIndex"/> uses for <c>Chunk</c>, applied to <c>Memory</c> so <c>memory_search</c>
/// can rank by semantic similarity to the query instead of only substring-matching Title/Body (see
/// <see cref="Context.Application.Adapters.SqliteMemoryVectorIndex"/>). One underlying collection per
/// embedding provider, same reasoning as <see cref="IChunkVectorIndex"/>.
/// </summary>
public interface IMemoryVectorIndex
{
    /// <summary>Adds or replaces the vector for one memory.</summary>
    Task UpsertAsync(Guid memoryId, Guid projectId, string provider, float[] vector, CancellationToken ct);

    /// <summary>
    /// The nearest memories to <paramref name="queryVector"/> for <paramref name="provider"/>,
    /// scoped to <paramref name="projectId"/>. Score is cosine similarity in [-1, 1] (higher is
    /// better), same convention as <see cref="IChunkVectorIndex.SearchAsync"/>.
    /// </summary>
    Task<IReadOnlyList<(Guid MemoryId, double Score)>> SearchAsync(
        Guid projectId, string provider, float[] queryVector, int limit, CancellationToken ct);

    /// <summary>
    /// Removes every vector-index record for <paramref name="projectId"/>, across every
    /// embedding-provider collection that exists in this database -- same reasoning as
    /// <see cref="IChunkVectorIndex.DeleteByProjectAsync"/>.
    /// </summary>
    Task DeleteByProjectAsync(Guid projectId, CancellationToken ct);
}
