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
/// <see cref="IMemoryVectorIndex"/> backed by sqlite-vec, mirroring <see cref="SqliteChunkVectorIndex"/>:
/// one collection per embedding model key (<c>memory_vec_{key}</c>), same connection/lifetime story.
/// </summary>
/// <remarks>
/// The first use of a collection in a process fills its gaps from <c>MemoryEmbeddings</c>: every live
/// memory embedded by the same model and dimension that has no record yet, which covers both a new
/// collection and a vector write lost after its database commit. A failed upsert repeats that fill on
/// the collection's next use. Rows embedded by another model are never mixed in. It also drops the pre-knowledge-model <c>memory_vectors_*</c> collections, which
/// carried vectors of unknown provenance.
/// </remarks>
public sealed class SqliteMemoryVectorIndex(string dbPath, IDbContextFactory<ContextDbContext> dbContextFactory)
    : IMemoryVectorIndex, IDisposable
{
    private readonly string _connectionString = $"Data Source={dbPath};Default Timeout=10";
    private readonly ConcurrentDictionary<string, Lazy<Task<SqliteCollection<Guid, MemoryVectorRecord>>>> _collections = new();
    private readonly ConcurrentDictionary<string, byte> _stale = new();

    public async Task UpsertAsync(Guid memoryId, Guid projectId, string? topic, string provider, float[] vector, CancellationToken ct)
    {
        var collection = await GetOrCreateCollectionAsync(provider, vector.Length, ct).ConfigureAwait(false);
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

    public async Task<IReadOnlyList<(Guid MemoryId, double Score)>> SearchAsync(
        Guid projectId, string provider, float[] queryVector, int limit, CancellationToken ct, string? topic = null)
    {
        var collection = await GetOrCreateCollectionAsync(provider, queryVector.Length, ct).ConfigureAwait(false);

        var normalizedTopic = topic?.ToLowerInvariant();
        var options = new VectorSearchOptions<MemoryVectorRecord>
        {
            Filter = normalizedTopic is null
                ? r => r.ProjectId == projectId
                : r => r.ProjectId == projectId && r.Topic == normalizedTopic,
        };
        var results = new List<(Guid, double)>();

        await foreach (var hit in collection.SearchAsync(new ReadOnlyMemory<float>(queryVector), limit, options, ct)
            .ConfigureAwait(false))
        {
            // Same [0, 2] cosine-distance -> similarity conversion as SqliteChunkVectorIndex.
            results.Add((hit.Record.MemoryId, 1.0 - (hit.Score ?? 1.0)));
        }

        return results;
    }

    public async Task DeleteByProjectAsync(Guid projectId, CancellationToken ct)
    {
        var tableNames = await SqliteVectorTables.ListAsync(_connectionString, TableNamePrefix, ct).ConfigureAwait(false);

        foreach (var tableName in tableNames)
        {
            using var collection = OpenForDelete(tableName);
            await collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);

            var memoryIds = new List<Guid>();
            await foreach (var record in collection.GetAsync(r => r.ProjectId == projectId, int.MaxValue, options: null, ct)
                .ConfigureAwait(false))
            {
                memoryIds.Add(record.MemoryId);
            }

            if (memoryIds.Count > 0)
            {
                await collection.DeleteAsync(memoryIds, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlySet<Guid>> ListMemoryIdsAsync(Guid projectId, CancellationToken ct)
    {
        var ids = new HashSet<Guid>();
        foreach (var tableName in await SqliteVectorTables.ListAsync(_connectionString, TableNamePrefix, ct).ConfigureAwait(false))
        {
            using var collection = OpenForDelete(tableName);
            await collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
            await foreach (var record in collection.GetAsync(r => r.ProjectId == projectId, int.MaxValue, options: null, ct).ConfigureAwait(false))
            {
                ids.Add(record.MemoryId);
            }
        }

        return ids;
    }

    public async Task DeleteAsync(IReadOnlyCollection<Guid> memoryIds, CancellationToken ct)
    {
        if (memoryIds.Count == 0)
        {
            return;
        }

        // Every provider collection, not just the one this memory was last embedded into: the
        // embedding provider can change between the upsert and the delete, and a key that isn't
        // there is a no-op delete anyway.
        var tableNames = await SqliteVectorTables.ListAsync(_connectionString, TableNamePrefix, ct).ConfigureAwait(false);

        foreach (var tableName in tableNames)
        {
            using var collection = OpenForDelete(tableName);
            await collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
            await collection.DeleteAsync(memoryIds, ct).ConfigureAwait(false);
        }
    }

    private SqliteCollection<Guid, MemoryVectorRecord> OpenForDelete(string tableName) =>
        new(_connectionString, tableName, new SqliteCollectionOptions { Definition = BuildDeleteOnlyDefinition() });

    private static VectorStoreCollectionDefinition BuildDeleteOnlyDefinition() => new()
    {
        Properties =
        {
            new VectorStoreKeyProperty(nameof(MemoryVectorRecord.MemoryId), typeof(Guid)),
            new VectorStoreDataProperty(nameof(MemoryVectorRecord.ProjectId), typeof(Guid)) { IsIndexed = true },
            new VectorStoreDataProperty(nameof(MemoryVectorRecord.Topic), typeof(string)),
            new VectorStoreVectorProperty(nameof(MemoryVectorRecord.Vector), typeof(ReadOnlyMemory<float>), 1)
            {
                DistanceFunction = DistanceFunction.CosineDistance,
            },
        },
    };

    public void Dispose()
    {
        foreach (var lazy in _collections.Values)
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
            {
                lazy.Value.Result.Dispose();
            }
        }
    }

    private async Task<SqliteCollection<Guid, MemoryVectorRecord>> GetOrCreateCollectionAsync(
        string provider, int dimensions, CancellationToken ct)
    {
        var lazy = _collections.GetOrAdd(
            provider,
            p => new Lazy<Task<SqliteCollection<Guid, MemoryVectorRecord>>>(
                () => CreateCollectionAsync(p, dimensions, CancellationToken.None)));
        SqliteCollection<Guid, MemoryVectorRecord> collection;
        try
        {
            collection = await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch when (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
        {
            // Same as SqliteChunkVectorIndex: a faulted init must not stick for the process lifetime.
            _collections.TryRemove(new KeyValuePair<string, Lazy<Task<SqliteCollection<Guid, MemoryVectorRecord>>>>(provider, lazy));
            throw;
        }

        if (_stale.TryRemove(provider, out _))
        {
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

        return collection;
    }

    private async Task<SqliteCollection<Guid, MemoryVectorRecord>> CreateCollectionAsync(
        string provider, int dimensions, CancellationToken ct)
    {
        var collection = BuildCollection(provider, dimensions);
        try
        {
            await collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
            await DropLegacyCollectionsAsync(ct).ConfigureAwait(false);
            await BackfillMissingAsync(collection, provider, dimensions, ct).ConfigureAwait(false);
            return collection;
        }
        catch
        {
            collection.Dispose();
            throw;
        }
    }

    private async Task DropLegacyCollectionsAsync(CancellationToken ct)
    {
        foreach (var tableName in await SqliteVectorTables.ListAsync(_connectionString, LegacyTableNamePrefix, ct).ConfigureAwait(false))
        {
            using var collection = OpenForDelete(tableName);
            await collection.EnsureCollectionDeletedAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task BackfillMissingAsync(
        SqliteCollection<Guid, MemoryVectorRecord> collection, string provider, int dimensions, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

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
            // With vectors: a failed upsert can leave the data row without its vector, which empties
            // every KNN search of the collection until the record is written again.
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

    private SqliteCollection<Guid, MemoryVectorRecord> BuildCollection(string provider, int dimensions)
    {
        var definition = new VectorStoreCollectionDefinition
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

        return new SqliteCollection<Guid, MemoryVectorRecord>(
            _connectionString, CollectionNameFor(provider), new SqliteCollectionOptions { Definition = definition });
    }

    private static readonly RecordRetrievalOptions WithVectors = new() { IncludeVectors = true };

    private const string TableNamePrefix = "memory_vec_";
    private const int BackfillBatchSize = 500;
    private const string LegacyTableNamePrefix = "memory_vectors_";

    private static string CollectionNameFor(string provider)
    {
        var sanitized = new string(provider.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return $"{TableNamePrefix}{(sanitized.Length > 0 ? sanitized : "default")}";
    }

    private sealed class MemoryVectorRecord
    {
        public required Guid MemoryId { get; set; }
        public required Guid ProjectId { get; set; }
        public string? Topic { get; set; }
        public required ReadOnlyMemory<float> Vector { get; set; }
    }
}
