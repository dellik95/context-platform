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
}
