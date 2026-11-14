using System.Collections.Concurrent;
using Context.Application.Abstractions;
using Context.Application.Support;
using Context.Domain.Enums;
using Context.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace Context.Application.Adapters;

/// <summary>
/// <see cref="IMemoryVectorIndex"/> backed by sqlite-vec (<c>memory_vec_{key}</c> per embedding
/// model key). Shares collection caching, search scoring, deletion sweeps, and disposal with
/// <see cref="SqliteVectorStore{TRecord}"/> while retaining model-keyed gap backfill and legacy
/// <c>memory_vectors_*</c> cleanup.
/// </summary>
public sealed class SqliteMemoryVectorIndex : IMemoryVectorIndex, IDisposable, IAsyncDisposable
{
    private const string TableNamePrefix = "memory_vec_";
    private const string LegacyTableNamePrefix = "memory_vectors_";
    private const int BackfillBatchSize = 500;
    private static readonly RecordRetrievalOptions WithVectors = new() { IncludeVectors = true };

    private readonly IDbContextFactory<ContextDbContext> _dbContextFactory;
    private readonly ConcurrentDictionary<string, byte> _stale = new();
    private readonly SqliteVectorStore<MemoryVectorRecord> _store;

    public SqliteMemoryVectorIndex(string dbPath, IDbContextFactory<ContextDbContext> dbContextFactory)
    {
        _dbContextFactory = dbContextFactory;
        _store = new SqliteVectorStore<MemoryVectorRecord>(
            dbPath,
            TableNamePrefix,
            BuildDefinition,
            r => r.MemoryId,
            InitializeCollectionAsync,
            RefreshIfStaleAsync);
    }

    public async Task UpsertAsync(Guid memoryId, Guid projectId, string? topic, string provider, float[] vector, CancellationToken ct)
    {
        var collection = await _store.GetOrCreateCollectionAsync(provider, vector.Length, ct).ConfigureAwait(false);
        try
        {
            await collection.UpsertAsync(
                // Lower-cased: Topic is only a case-insensitive filter key, never read back for display.
                new MemoryVectorRecord { MemoryId = memoryId, ProjectId = projectId, Topic = topic?.ToLowerInvariant(), Vector = vector },
                ct).ConfigureAwait(false);
        }
        catch
        {
            _stale.TryAdd(provider, 0);
            throw;
        }
    }

    public Task<IReadOnlyList<(Guid MemoryId, double Score)>> SearchAsync(
        Guid projectId, string provider, float[] queryVector, int limit, CancellationToken ct, string? topic = null)
    {
        var normalizedTopic = topic?.ToLowerInvariant();
        return _store.SearchAsync(
            provider,
            queryVector,
            limit,
            normalizedTopic is null
                ? r => r.ProjectId == projectId
                : r => r.ProjectId == projectId && r.Topic == normalizedTopic,
            ct);
    }

    public Task DeleteByProjectAsync(Guid projectId, CancellationToken ct) =>
        _store.DeleteByFilterAsync(r => r.ProjectId == projectId, ct);

    public Task<IReadOnlySet<Guid>> ListMemoryIdsAsync(Guid projectId, CancellationToken ct) =>
        _store.ListKeysByFilterAsync(r => r.ProjectId == projectId, ct);

    public Task DeleteAsync(IReadOnlyCollection<Guid> memoryIds, CancellationToken ct) =>
        _store.DeleteKeysAsync(memoryIds, ct);

    public void Dispose() => _store.Dispose();

    public ValueTask DisposeAsync() => _store.DisposeAsync();

    private async Task InitializeCollectionAsync(
        string provider, SqliteCollection<Guid, MemoryVectorRecord> collection, int dimensions, CancellationToken ct)
    {
        await collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
        await DropLegacyCollectionsAsync(ct).ConfigureAwait(false);
        await BackfillMissingAsync(collection, provider, dimensions, ct).ConfigureAwait(false);
    }

    private async Task RefreshIfStaleAsync(
        string provider, SqliteCollection<Guid, MemoryVectorRecord> collection, int dimensions, CancellationToken ct)
    {
        if (!_stale.TryRemove(provider, out _))
        {
            return;
        }

        try
        {
            await BackfillMissingAsync(collection, provider, dimensions, ct).ConfigureAwait(false);
        }
        catch
        {
            _stale.TryAdd(provider, 0);
            throw;
        }
    }

    private async Task DropLegacyCollectionsAsync(CancellationToken ct)
    {
        foreach (var tableName in await SqliteVectorTables.ListAsync(_store.ConnectionString, LegacyTableNamePrefix, ct).ConfigureAwait(false))
        {
            using var collection = _store.OpenCollection(tableName);
            await collection.EnsureCollectionDeletedAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task BackfillMissingAsync(
        SqliteCollection<Guid, MemoryVectorRecord> collection, string provider, int dimensions, CancellationToken ct)
    {
        await using var db = await _dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var embedded =
            from m in db.Memories.AsNoTracking()
            join e in db.MemoryEmbeddings.AsNoTracking() on m.Id equals e.MemoryId
            where e.ModelKey == provider
                && e.Dim == dimensions
                && (m.Status == MemoryStatus.Active || m.Status == MemoryStatus.Contested)
            select new { MemoryId = m.Id, m.ProjectId, m.Topic, e.Vector };

        var ids = await embedded.Select(r => r.MemoryId).ToListAsync(ct).ConfigureAwait(false);
        foreach (var batch in ids.Chunk(BackfillBatchSize))
        {
            // IncludeVectors: a failed upsert can leave a data row without its vector.
            var present = new HashSet<Guid>();
            await foreach (var record in collection.GetAsync(batch, WithVectors, ct).ConfigureAwait(false))
            {
                present.Add(record.MemoryId);
            }

            var missing = batch.Where(id => !present.Contains(id)).ToList();
            if (missing.Count == 0)
            {
                continue;
            }

            var rows = await embedded.Where(r => missing.Contains(r.MemoryId)).ToListAsync(ct).ConfigureAwait(false);
            await collection.UpsertAsync(
                rows.Select(r => new MemoryVectorRecord
                {
                    MemoryId = r.MemoryId,
                    ProjectId = r.ProjectId,
                    Topic = r.Topic?.ToLowerInvariant(),
                    Vector = new ReadOnlyMemory<float>(EmbeddingVectorCodec.Decode(r.Vector)),
                }),
                ct).ConfigureAwait(false);
        }
    }

    private static VectorStoreCollectionDefinition BuildDefinition(int dimensions) => new()
    {
        Properties =
        {
            new VectorStoreKeyProperty(nameof(MemoryVectorRecord.MemoryId), typeof(Guid)),
            new VectorStoreDataProperty(nameof(MemoryVectorRecord.ProjectId), typeof(Guid)) { IsIndexed = true },
            new VectorStoreDataProperty(nameof(MemoryVectorRecord.Topic), typeof(string)) { IsIndexed = true },
            new VectorStoreVectorProperty(nameof(MemoryVectorRecord.Vector), typeof(ReadOnlyMemory<float>), dimensions)
            {
                DistanceFunction = DistanceFunction.CosineDistance,
            },
        },
    };

    private sealed class MemoryVectorRecord
    {
        public required Guid MemoryId { get; set; }
        public required Guid ProjectId { get; set; }
        public string? Topic { get; set; }
        public required ReadOnlyMemory<float> Vector { get; set; }
    }
}
