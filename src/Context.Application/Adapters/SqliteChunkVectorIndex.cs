using System.Collections.Concurrent;
using Context.Application.Abstractions;
using Context.Application.Support;
using Context.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace Context.Application.Adapters;

/// <summary>
/// <see cref="IChunkVectorIndex"/> backed by sqlite-vec (<c>chunk_vectors_{provider}</c> per
/// embedding provider). Delegates shared collection lifecycle, ANN cosine scoring, deletion sweeps,
/// and disposal to <see cref="SqliteVectorStore{TRecord}"/> while keeping chunk backfill from the
/// <c>Embeddings</c> content-hash cache local to this class.
/// </summary>
public sealed class SqliteChunkVectorIndex : IChunkVectorIndex, IDisposable, IAsyncDisposable
{
    private const string TableNamePrefix = "chunk_vectors_";
    private const int BackfillBatchSize = 500;

    private readonly IDbContextFactory<ContextDbContext> _dbContextFactory;
    private readonly ConcurrentDictionary<string, byte> _pendingBackfills = new();
    private readonly SqliteVectorStore<ChunkVectorRecord> _store;

    public SqliteChunkVectorIndex(string dbPath, IDbContextFactory<ContextDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
        _store = new SqliteVectorStore<ChunkVectorRecord>(
            dbPath,
            TableNamePrefix,
            BuildDefinition,
            r => r.ChunkId,
            InitializeCollectionAsync);
    }

    public async Task UpsertAsync(Guid chunkId, Guid projectId, string provider, float[] vector, CancellationToken ct)
    {
        var collection = await _store.GetOrCreateCollectionAsync(provider, vector.Length, ct).ConfigureAwait(false);
        await collection.UpsertAsync(
            new ChunkVectorRecord { ChunkId = chunkId, ProjectId = projectId, Vector = vector },
            ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<(Guid ChunkId, double Score)>> SearchAsync(
        Guid projectId, string provider, float[] queryVector, int limit, CancellationToken ct) =>
        _store.SearchAsync(provider, queryVector, limit, r => r.ProjectId == projectId, ct);

    public Task DeleteByProjectAsync(Guid projectId, CancellationToken ct) =>
        _store.DeleteByFilterAsync(r => r.ProjectId == projectId, ct);

    public Task<IReadOnlySet<Guid>> ListChunkIdsAsync(Guid projectId, CancellationToken ct) =>
        _store.ListKeysByFilterAsync(r => r.ProjectId == projectId, ct);

    public Task DeleteAsync(IReadOnlyCollection<Guid> chunkIds, CancellationToken ct) =>
        _store.DeleteKeysAsync(chunkIds, ct);

    public void Dispose() => _store.Dispose();

    public ValueTask DisposeAsync() => _store.DisposeAsync();

    private async Task InitializeCollectionAsync(
        string provider, SqliteCollection<Guid, ChunkVectorRecord> collection, int dimensions, CancellationToken ct)
    {
        // Track pending backfill before EnsureCollectionExistsAsync creates the table so a retry
        // after an interrupted backfill still populates missing cache vectors.
        if (!await collection.CollectionExistsAsync(ct).ConfigureAwait(false))
        {
            _pendingBackfills.TryAdd(provider, 0);
        }

        await collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
        if (_pendingBackfills.ContainsKey(provider))
        {
            await BackfillFromEmbeddingCacheAsync(collection, provider, ct).ConfigureAwait(false);
            _pendingBackfills.TryRemove(provider, out _);
        }
    }

    private async Task BackfillFromEmbeddingCacheAsync(
        SqliteCollection<Guid, ChunkVectorRecord> collection, string provider, CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var rowsQuery =
            from c in db.Chunks.AsNoTracking()
            join f in db.Files.AsNoTracking() on c.FileId equals f.Id
            join e in db.Embeddings.AsNoTracking() on c.ContentHash equals e.ContentHash
            where e.Provider == provider && !f.IsDeleted
            select new { ChunkId = c.Id, f.ProjectId, e.Vector };

        var batch = new List<ChunkVectorRecord>(BackfillBatchSize);
        await foreach (var row in rowsQuery.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            batch.Add(new ChunkVectorRecord
            {
                ChunkId = row.ChunkId,
                ProjectId = row.ProjectId,
                Vector = new ReadOnlyMemory<float>(EmbeddingVectorCodec.Decode(row.Vector)),
            });

            if (batch.Count >= BackfillBatchSize)
            {
                await collection.UpsertAsync(batch, ct).ConfigureAwait(false);
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await collection.UpsertAsync(batch, ct).ConfigureAwait(false);
        }
    }

    private static VectorStoreCollectionDefinition BuildDefinition(int dimensions) => new()
    {
        Properties =
        {
            new VectorStoreKeyProperty(nameof(ChunkVectorRecord.ChunkId), typeof(Guid)),
            new VectorStoreDataProperty(nameof(ChunkVectorRecord.ProjectId), typeof(Guid)) { IsIndexed = true },
            new VectorStoreVectorProperty(nameof(ChunkVectorRecord.Vector), typeof(ReadOnlyMemory<float>), dimensions)
            {
                DistanceFunction = DistanceFunction.CosineDistance,
            },
        },
    };

    private sealed class ChunkVectorRecord
    {
        public required Guid ChunkId { get; set; }
        public required Guid ProjectId { get; set; }
        public required ReadOnlyMemory<float> Vector { get; set; }
    }
}
