using System.Collections.Concurrent;
using Context.Application.Abstractions;
using Context.Application.Support;
using Context.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace Context.Application.Adapters;

/// <summary>
/// <see cref="IMemoryVectorIndex"/> backed by sqlite-vec, mirroring <see cref="SqliteChunkVectorIndex"/>
/// exactly -- same reasoning for per-provider collections (<c>memory_vectors_{provider}</c>),
/// same connection/lifetime story. See that class's remarks for the full rationale; only the
/// differences are called out here.
/// </summary>
/// <remarks>
/// <b>Backfill is provider-blind, unlike the chunk side:</b> <see cref="Context.Domain.Entities.MemoryEmbedding"/>
/// has no <c>Provider</c> column (it predates this index and was only ever read by
/// <see cref="Context.Memory.Extraction.MemoryConsolidator"/>'s brute-force, provider-blind cosine
/// dedup) -- so on first use, every existing <c>MemoryEmbeddings</c> row is backfilled into
/// whichever provider's collection is created first, on the same implicit assumption the
/// consolidator already makes: a project has exactly one active embedding provider at a time. If a
/// project later switches providers, old vectors keep the stale dimensionality's collection; new
/// memories embed into the new provider's collection going forward, same tradeoff
/// <see cref="SqliteChunkVectorIndex"/> accepts for chunks (whose Embedding cache *does* key by
/// provider, but whose vector-index collections still each start from that one shared cache).
/// </remarks>
public sealed class SqliteMemoryVectorIndex(string dbPath, IDbContextFactory<ContextDbContext> dbContextFactory)
    : IMemoryVectorIndex, IDisposable
{
    private readonly string _connectionString = $"Data Source={dbPath};Default Timeout=10";
    private readonly ConcurrentDictionary<string, Lazy<Task<SqliteCollection<Guid, MemoryVectorRecord>>>> _collections = new();

    public async Task UpsertAsync(Guid memoryId, Guid projectId, string provider, float[] vector, CancellationToken ct)
    {
        var collection = await GetOrCreateCollectionAsync(provider, vector.Length, ct).ConfigureAwait(false);
        await collection.UpsertAsync(
            new MemoryVectorRecord { MemoryId = memoryId, ProjectId = projectId, Vector = vector },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(Guid MemoryId, double Score)>> SearchAsync(
        Guid projectId, string provider, float[] queryVector, int limit, CancellationToken ct)
    {
        var collection = await GetOrCreateCollectionAsync(provider, queryVector.Length, ct).ConfigureAwait(false);

        var options = new VectorSearchOptions<MemoryVectorRecord> { Filter = r => r.ProjectId == projectId };
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
        var tableNames = await ListMemoryVectorTableNamesAsync(ct).ConfigureAwait(false);

        foreach (var tableName in tableNames)
        {
            using var collection = new SqliteCollection<Guid, MemoryVectorRecord>(
                _connectionString, tableName, new SqliteCollectionOptions { Definition = BuildDeleteOnlyDefinition() });
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

    private async Task<IReadOnlyList<string>> ListMemoryVectorTableNamesAsync(CancellationToken ct)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'memory\\_vectors\\_%' ESCAPE '\\';";

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static VectorStoreCollectionDefinition BuildDeleteOnlyDefinition() => new()
    {
        Properties =
        {
            new VectorStoreKeyProperty(nameof(MemoryVectorRecord.MemoryId), typeof(Guid)),
            new VectorStoreDataProperty(nameof(MemoryVectorRecord.ProjectId), typeof(Guid)) { IsIndexed = true },
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

    private Task<SqliteCollection<Guid, MemoryVectorRecord>> GetOrCreateCollectionAsync(
        string provider, int dimensions, CancellationToken ct) =>
        _collections.GetOrAdd(
            provider,
            p => new Lazy<Task<SqliteCollection<Guid, MemoryVectorRecord>>>(() => CreateCollectionAsync(p, dimensions, ct))
        ).Value;

    private async Task<SqliteCollection<Guid, MemoryVectorRecord>> CreateCollectionAsync(
        string provider, int dimensions, CancellationToken ct)
    {
        var collection = BuildCollection(provider, dimensions);

        var existedBefore = await collection.CollectionExistsAsync(ct).ConfigureAwait(false);
        await collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
        if (!existedBefore)
        {
            await BackfillFromMemoryEmbeddingsAsync(collection, ct).ConfigureAwait(false);
        }

        return collection;
    }

    private async Task BackfillFromMemoryEmbeddingsAsync(
        SqliteCollection<Guid, MemoryVectorRecord> collection, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var rows = await (
            from m in db.Memories.AsNoTracking()
            join e in db.MemoryEmbeddings.AsNoTracking() on m.Id equals e.MemoryId
            select new { MemoryId = m.Id, m.ProjectId, e.Vector }
        ).ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return;
        }

        var records = rows.Select(r => new MemoryVectorRecord
        {
            MemoryId = r.MemoryId,
            ProjectId = r.ProjectId,
            Vector = new ReadOnlyMemory<float>(EmbeddingVectorCodec.Decode(r.Vector)),
        });

        await collection.UpsertAsync(records, ct).ConfigureAwait(false);
    }

    private SqliteCollection<Guid, MemoryVectorRecord> BuildCollection(string provider, int dimensions)
    {
        var definition = new VectorStoreCollectionDefinition
        {
            Properties =
            {
                new VectorStoreKeyProperty(nameof(MemoryVectorRecord.MemoryId), typeof(Guid)),
                new VectorStoreDataProperty(nameof(MemoryVectorRecord.ProjectId), typeof(Guid)) { IsIndexed = true },
                new VectorStoreVectorProperty(nameof(MemoryVectorRecord.Vector), typeof(ReadOnlyMemory<float>), dimensions)
                {
                    DistanceFunction = DistanceFunction.CosineDistance,
                },
            },
        };

        return new SqliteCollection<Guid, MemoryVectorRecord>(
            _connectionString, CollectionNameFor(provider), new SqliteCollectionOptions { Definition = definition });
    }

    private static string CollectionNameFor(string provider)
    {
        var sanitized = new string(provider.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return $"memory_vectors_{(sanitized.Length > 0 ? sanitized : "default")}";
    }

    private sealed class MemoryVectorRecord
    {
        public required Guid MemoryId { get; set; }
        public required Guid ProjectId { get; set; }
        public required ReadOnlyMemory<float> Vector { get; set; }
    }
}
