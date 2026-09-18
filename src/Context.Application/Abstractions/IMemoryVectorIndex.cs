namespace Context.Application.Abstractions;

/// <summary>
/// Nearest-neighbor vector search over memory embeddings, backed by sqlite-vec -- the same pattern
/// <see cref="IChunkVectorIndex"/> uses for <c>Chunk</c>, applied to <c>Memory</c> so <c>memory_search</c>
/// can rank by semantic similarity to the query instead of only substring-matching Title/Body (see
/// <see cref="Context.Application.Adapters.SqliteMemoryVectorIndex"/>). One underlying collection per
/// embedding provider, same reasoning as <see cref="IChunkVectorIndex"/>.
/// </summary>
/// <remarks>
/// <b>Unlike <see cref="IChunkVectorIndex"/>, this record carries <c>Type</c>/<c>Topic</c> metadata
/// alongside the vector</b> (see <see cref="Context.Application.Adapters.SqliteMemoryVectorIndex"/>'s
/// remarks for why the two indexes differ here): a <c>Memory</c> row is never silently replaced the
/// way a re-parsed <c>Chunk</c> is, so there's no "stale denormalized field" risk to weigh against
/// the real benefit -- <paramref name="topic"/>-scoped calls to <see cref="SearchAsync"/> can filter
/// during the ANN search itself instead of only after a fixed top-K cut already truncated the
/// candidate set (the gap <see cref="IChunkVectorIndex.SearchAsync"/> still has, since it has no
/// metadata to filter on).
/// </remarks>
public interface IMemoryVectorIndex
{
    /// <summary>Adds or replaces the vector (and its <paramref name="memoryType"/>/<paramref name="topic"/>
    /// metadata) for one memory.</summary>
    Task UpsertAsync(Guid memoryId, Guid projectId, string memoryType, string? topic, string provider, float[] vector, CancellationToken ct);

    /// <summary>
    /// The nearest memories to <paramref name="queryVector"/> for <paramref name="provider"/>,
    /// scoped to <paramref name="projectId"/> and, when <paramref name="topic"/> is non-null,
    /// additionally restricted to that <c>Memory.Topic</c> (case-insensitive, exact match -- same
    /// semantics as <see cref="Context.Infrastructure.Abstractions.IMemoryRepository.SearchAsync"/>'s
    /// topic filter). Score is cosine similarity in [-1, 1] (higher is better), same convention as
    /// <see cref="IChunkVectorIndex.SearchAsync"/>.
    /// </summary>
    Task<IReadOnlyList<(Guid MemoryId, double Score)>> SearchAsync(
        Guid projectId, string provider, float[] queryVector, int limit, CancellationToken ct, string? topic = null);

    /// <summary>
    /// Removes every vector-index record for <paramref name="projectId"/>, across every
    /// embedding-provider collection that exists in this database -- same reasoning as
    /// <see cref="IChunkVectorIndex.DeleteByProjectAsync"/>.
    /// </summary>
    Task DeleteByProjectAsync(Guid projectId, CancellationToken ct);

    /// <summary>
    /// Removes the records for <paramref name="memoryIds"/> from every embedding-provider
    /// collection. Ids that aren't indexed are ignored. Same recall argument as
    /// <see cref="IChunkVectorIndex.DeleteAsync"/>: a deleted memory drops out of the join anyway,
    /// but until its vector goes too it keeps consuming a top-K slot.
    /// </summary>
    Task DeleteAsync(IReadOnlyCollection<Guid> memoryIds, CancellationToken ct);
}
