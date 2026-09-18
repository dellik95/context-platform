using Context.Application.Abstractions;
using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;

namespace Context.Application.Adapters;

/// <summary>
/// Decorates <see cref="IChunkRepository"/> so dropping a chunk row also drops its sqlite-vec
/// record. The vector store is a separate table with no foreign key back to <c>Chunks</c>, so
/// nothing else would ever remove those rows — and a file is re-chunked on every content change,
/// which makes this the highest-churn source of orphaned vectors in the database.
/// </summary>
/// <remarks>A decorator rather than a call at each site: <c>IndexingPipeline</c> alone replaces a
/// file's chunks from five places, and a site added later would silently start leaking again.</remarks>
public sealed class VectorPruningChunkRepository(IChunkRepository inner, IChunkVectorIndex vectorIndex) : IChunkRepository
{
    public async Task<IReadOnlyList<Guid>> ReplaceForFileAsync(Guid fileId, IReadOnlyList<Chunk> chunks, CancellationToken ct)
    {
        var removed = await inner.ReplaceForFileAsync(fileId, chunks, ct).ConfigureAwait(false);

        // Re-indexing reuses a chunk's id when its content is unchanged, and those rows are
        // re-inserted by the same call — pruning them would throw away a still-valid vector.
        var kept = chunks.Select(c => c.Id).ToHashSet();
        Guid[] stale = [.. removed.Where(id => !kept.Contains(id))];
        if (stale.Length > 0)
        {
            await vectorIndex.DeleteAsync(stale, ct).ConfigureAwait(false);
        }

        return removed;
    }

    public Task<Chunk?> GetByIdAsync(Guid id, CancellationToken ct) => inner.GetByIdAsync(id, ct);

    public Task<IReadOnlyList<Chunk>> ListByFileAsync(Guid fileId, CancellationToken ct) => inner.ListByFileAsync(fileId, ct);

    public Task<IReadOnlyList<Chunk>> GetByContentHashesAsync(IEnumerable<string> contentHashes, CancellationToken ct) =>
        inner.GetByContentHashesAsync(contentHashes, ct);

    public Task<IReadOnlyList<Chunk>> ListByProjectAsync(Guid projectId, CancellationToken ct) => inner.ListByProjectAsync(projectId, ct);
}
