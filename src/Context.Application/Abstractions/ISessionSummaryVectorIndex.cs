namespace Context.Application.Abstractions;

/// <summary>
/// Nearest-neighbor vector search over <see cref="Context.Domain.Entities.SessionSummaryChunk"/>
/// embeddings, backed by sqlite-vec -- the same pattern <see cref="IMemoryVectorIndex"/> uses for
/// <c>Memory</c>, applied to session-summary chunks so <c>session_summary_search</c> can rank by
/// semantic similarity to the query instead of only substring-matching (see
/// <see cref="Context.Application.Adapters.SqliteSessionSummaryVectorIndex"/>). One underlying
/// collection per embedding provider, same reasoning as <see cref="IMemoryVectorIndex"/>.
/// </summary>
public interface ISessionSummaryVectorIndex
{
    /// <summary>Adds or replaces the vector (and its <paramref name="sessionId"/> metadata) for one chunk.</summary>
    Task UpsertAsync(Guid chunkId, Guid projectId, Guid sessionId, string provider, float[] vector, CancellationToken ct);

    /// <summary>
    /// The nearest chunks to <paramref name="queryVector"/> for <paramref name="provider"/>,
    /// scoped to <paramref name="projectId"/> and, when <paramref name="sessionId"/> is non-null,
    /// additionally restricted to that session's own chunks. Score is cosine similarity in
    /// [-1, 1] (higher is better), same convention as <see cref="IMemoryVectorIndex.SearchAsync"/>.
    /// </summary>
    Task<IReadOnlyList<(Guid ChunkId, double Score)>> SearchAsync(
        Guid projectId, string provider, float[] queryVector, int limit, CancellationToken ct, Guid? sessionId = null);

    /// <summary>
    /// Removes every vector-index record for <paramref name="projectId"/>, across every
    /// embedding-provider collection that exists in this database -- same reasoning as
    /// <see cref="IMemoryVectorIndex.DeleteByProjectAsync"/>.
    /// </summary>
    Task DeleteByProjectAsync(Guid projectId, CancellationToken ct);
}
