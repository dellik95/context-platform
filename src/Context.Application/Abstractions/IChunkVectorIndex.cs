namespace Context.Application.Abstractions;

/// <summary>
/// Nearest-neighbor vector search over chunk embeddings, backed by a real ANN index (sqlite-vec)
/// instead of a brute-force cosine scan over every row -- see
/// <see cref="Context.Application.Adapters.SqliteChunkVectorIndex"/>. One underlying collection
/// per embedding provider (dimensionality varies by provider/model, e.g. 768 for Ollama's
/// embeddinggemma vs. 1536 for OpenAI's text-embedding-3-small); <c>projectId</c> is an indexed
/// filter column within a provider's collection, not a separate partition.
/// </summary>
public interface IChunkVectorIndex
{
    /// <summary>Adds or replaces the vector for one chunk.</summary>
    Task UpsertAsync(Guid chunkId, Guid projectId, string provider, float[] vector, CancellationToken ct);

    /// <summary>
    /// The nearest chunks to <paramref name="queryVector"/> for <paramref name="provider"/>,
    /// scoped to <paramref name="projectId"/>. Score is cosine similarity in [-1, 1] (higher is
    /// better) -- the same convention <c>Context.Retrieval.Abstractions.ISearchIndex</c>'s
    /// CosineScore already uses.
    /// </summary>
    Task<IReadOnlyList<(Guid ChunkId, double Score)>> SearchAsync(
        Guid projectId, string provider, float[] queryVector, int limit, CancellationToken ct);

    /// <summary>
    /// Removes every vector-index record for <paramref name="projectId"/>, across every
    /// embedding-provider collection that exists in this database. Used when deleting a project:
    /// this table is a separate sqlite-vec-backed store, not an EF-mapped entity, so Project's
    /// cascade-delete FK configuration never reaches it (see <c>Context.Host.Api.ProjectEndpoints</c>'s
    /// <c>DELETE /api/projects/{id}</c>).
    /// </summary>
    Task DeleteByProjectAsync(Guid projectId, CancellationToken ct);

    /// <summary>
    /// Removes the records for <paramref name="chunkIds"/> from every embedding-provider
    /// collection. Ids that aren't indexed are ignored.
    /// </summary>
    /// <remarks>Search joins hits back to the live <c>Chunks</c> table, so an orphaned record can't
    /// produce a wrong result — but it still occupies one of the ANN pass's fixed top-K slots, so a
    /// project reindexed enough times loses recall to rows whose chunks are long gone.</remarks>
    Task DeleteAsync(IReadOnlyCollection<Guid> chunkIds, CancellationToken ct);
}
