using System.Collections.Concurrent;
using Context.Application.Abstractions;
using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.Logging;

namespace Context.Application.Adapters;

/// <summary>
/// Decorates <see cref="IChunkRepository"/> so dropping a chunk row also drops its sqlite-vec
/// record. The vector store is a separate table with no foreign key back to <c>Chunks</c>, so
/// nothing else would ever remove those rows — and a file is re-chunked on every content change,
/// which makes this the highest-churn source of orphaned vectors in the database.
/// </summary>
/// <remarks>A decorator rather than a call at each site: <c>IndexingPipeline</c> alone replaces a
/// file's chunks from five places, and a site added later would silently start leaking again.</remarks>
public sealed class VectorPruningChunkRepository(
    IChunkRepository inner, IChunkVectorIndex vectorIndex, ILogger<VectorPruningChunkRepository>? logger = null) : IChunkRepository
{
    private readonly ConcurrentDictionary<Guid, DeferredFileBatch> _deferredBatches = new();

    /// <summary>
    /// Defers sqlite-vec deletions for <paramref name="fileId"/> across the FK-clear + re-insert
    /// sequence so unchanged chunk IDs reused in the second <see cref="ReplaceForFileAsync"/> call
    /// are NOT unnecessarily deleted from the vector store.
    /// </summary>
    internal DeferredPruningScope BeginFileUpdate(Guid fileId)
    {
        var batch = new DeferredFileBatch();
        _deferredBatches[fileId] = batch;
        return new DeferredPruningScope(this, fileId, batch);
    }

    public async Task<IReadOnlyList<Guid>> ReplaceForFileAsync(Guid fileId, IReadOnlyList<Chunk> chunks, CancellationToken ct)
    {
        var removed = await inner.ReplaceForFileAsync(fileId, chunks, ct).ConfigureAwait(false);

        if (_deferredBatches.TryGetValue(fileId, out var batch))
        {
            batch.Record(removed, chunks);
            return removed;
        }

        // Re-indexing reuses a chunk's id when its content is unchanged, and those rows are
        // re-inserted by the same call — pruning them would throw away a still-valid vector.
        var kept = chunks.Select(c => c.Id).ToHashSet();
        Guid[] stale = [.. removed.Where(id => !kept.Contains(id))];
        await PruneAsync(fileId, stale, ct).ConfigureAwait(false);

        return removed;
    }

    private async Task PruneAsync(Guid fileId, Guid[] stale, CancellationToken ct)
    {
        if (stale.Length == 0)
        {
            return;
        }

        // The rows are already gone and the removed ids cannot be recovered later, so a failure here
        // must not fail the caller (its retry would find nothing left to prune): the orphaned vectors are
        // swept by RepairVectors instead.
        try
        {
            await vectorIndex.DeleteAsync(stale, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Could not prune {Count} stale chunk vectors for file {FileId}; a vector repair pass will remove them.", stale.Length, fileId);
        }
    }

    internal sealed class DeferredPruningScope(VectorPruningChunkRepository owner, Guid fileId, DeferredFileBatch batch)
    {
        private bool _flushed;

        public async Task FlushAsync(CancellationToken ct)
        {
            if (_flushed)
            {
                return;
            }

            _flushed = true;
            owner._deferredBatches.TryRemove(new KeyValuePair<Guid, DeferredFileBatch>(fileId, batch));
            var stale = batch.TakeStaleIds();
            await owner.PruneAsync(fileId, stale, ct).ConfigureAwait(false);
        }
    }

    internal sealed class DeferredFileBatch
    {
        private readonly HashSet<Guid> _removed = [];
        private readonly HashSet<Guid> _kept = [];
        private readonly Lock _gate = new();

        public void Record(IReadOnlyList<Guid> removed, IReadOnlyList<Chunk> chunks)
        {
            lock (_gate)
            {
                foreach (var id in removed)
                {
                    _removed.Add(id);
                }

                foreach (var chunk in chunks)
                {
                    _kept.Add(chunk.Id);
                }
            }
        }

        public Guid[] TakeStaleIds()
        {
            lock (_gate)
            {
                return [.. _removed.Where(id => !_kept.Contains(id))];
            }
        }
    }

    public Task<Chunk?> GetByIdAsync(Guid id, CancellationToken ct) => inner.GetByIdAsync(id, ct);

    public Task<IReadOnlyList<Chunk>> ListByFileAsync(Guid fileId, CancellationToken ct) => inner.ListByFileAsync(fileId, ct);

    public Task<IReadOnlyList<Chunk>> GetByContentHashesAsync(IEnumerable<string> contentHashes, CancellationToken ct) =>
        inner.GetByContentHashesAsync(contentHashes, ct);

    public Task<IReadOnlyList<Chunk>> ListByProjectAsync(Guid projectId, CancellationToken ct) => inner.ListByProjectAsync(projectId, ct);

    public Task<IReadOnlySet<Guid>> ListIdsByProjectAsync(Guid projectId, CancellationToken ct) => inner.ListIdsByProjectAsync(projectId, ct);
}
