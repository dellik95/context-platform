using System.Collections.Concurrent;
using Context.Application.Abstractions;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace Context.Application.Adapters;

/// <summary>
/// <see cref="ISessionSummaryVectorIndex"/> backed by sqlite-vec, mirroring
/// <see cref="SqliteMemoryVectorIndex"/> exactly -- same reasoning for per-provider collections
/// (<c>session_summary_vectors_{provider}</c>), same connection/lifetime story. See that class's
/// remarks for the full rationale; only the differences are called out here.
/// </summary>
/// <remarks>
/// <b>No backfill, unlike the memory side:</b> there is no pre-existing raw-embedding table to seed
/// from -- chunks only ever get embedded going forward, from
/// <see cref="Context.Application.Jobs.Handlers.SummarizeSessionJobHandler"/> right after each new
/// <see cref="Context.Domain.Entities.SessionSummaryChunk"/> is persisted. There's also no separate
/// raw-storage EF table (<c>MemoryEmbedding</c>'s equivalent): nothing else needs to read the raw
/// floats back out, so this sqlite-vec collection is the only persistent store for chunk vectors.
/// </remarks>
public sealed class SqliteSessionSummaryVectorIndex(string dbPath)
    : ISessionSummaryVectorIndex, IDisposable
{
    private readonly string _connectionString = $"Data Source={dbPath};Default Timeout=10";
    private readonly ConcurrentDictionary<string, Lazy<Task<SqliteCollection<Guid, SessionSummaryVectorRecord>>>> _collections = new();

    public async Task UpsertAsync(Guid chunkId, Guid projectId, Guid sessionId, string provider, float[] vector, CancellationToken ct)
    {
        var collection = await GetOrCreateCollectionAsync(provider, vector.Length, ct).ConfigureAwait(false);
        await collection.UpsertAsync(
            new SessionSummaryVectorRecord { ChunkId = chunkId, ProjectId = projectId, SessionId = sessionId, Vector = vector },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(Guid ChunkId, double Score)>> SearchAsync(
        Guid projectId, string provider, float[] queryVector, int limit, CancellationToken ct, Guid? sessionId = null)
    {
        var collection = await GetOrCreateCollectionAsync(provider, queryVector.Length, ct).ConfigureAwait(false);

        var options = new VectorSearchOptions<SessionSummaryVectorRecord>
        {
            Filter = sessionId is null
                ? r => r.ProjectId == projectId
                : r => r.ProjectId == projectId && r.SessionId == sessionId,
        };
        var results = new List<(Guid, double)>();

        await foreach (var hit in collection.SearchAsync(new ReadOnlyMemory<float>(queryVector), limit, options, ct)
            .ConfigureAwait(false))
        {
            // Same [0, 2] cosine-distance -> similarity conversion as SqliteMemoryVectorIndex.
            results.Add((hit.Record.ChunkId, 1.0 - (hit.Score ?? 1.0)));
        }

        return results;
    }

    public async Task DeleteByProjectAsync(Guid projectId, CancellationToken ct)
    {
        var tableNames = await ListSessionSummaryVectorTableNamesAsync(ct).ConfigureAwait(false);

        foreach (var tableName in tableNames)
        {
            using var collection = new SqliteCollection<Guid, SessionSummaryVectorRecord>(
                _connectionString, tableName, new SqliteCollectionOptions { Definition = BuildDeleteOnlyDefinition() });
            await collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);

            var chunkIds = new List<Guid>();
            await foreach (var record in collection.GetAsync(r => r.ProjectId == projectId, int.MaxValue, options: null, ct)
                .ConfigureAwait(false))
            {
                chunkIds.Add(record.ChunkId);
            }

            if (chunkIds.Count > 0)
            {
                await collection.DeleteAsync(chunkIds, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlySet<Guid>> ListChunkIdsAsync(Guid projectId, string provider, CancellationToken ct)
    {
        var tableName = CollectionNameFor(provider);
        var ids = new HashSet<Guid>();
        if (!(await ListSessionSummaryVectorTableNamesAsync(ct).ConfigureAwait(false)).Contains(tableName))
        {
            return ids;
        }

        using var collection = new SqliteCollection<Guid, SessionSummaryVectorRecord>(
            _connectionString, tableName, new SqliteCollectionOptions { Definition = BuildDeleteOnlyDefinition() });
        await collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
        await foreach (var record in collection.GetAsync(r => r.ProjectId == projectId, int.MaxValue, options: null, ct).ConfigureAwait(false))
        {
            ids.Add(record.ChunkId);
        }

        return ids;
    }

    private async Task<IReadOnlyList<string>> ListSessionSummaryVectorTableNamesAsync(CancellationToken ct)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'session\\_summary\\_vectors\\_%' ESCAPE '\\';";

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
            new VectorStoreKeyProperty(nameof(SessionSummaryVectorRecord.ChunkId), typeof(Guid)),
            new VectorStoreDataProperty(nameof(SessionSummaryVectorRecord.ProjectId), typeof(Guid)) { IsIndexed = true },
            new VectorStoreDataProperty(nameof(SessionSummaryVectorRecord.SessionId), typeof(Guid)) { IsIndexed = true },
            new VectorStoreVectorProperty(nameof(SessionSummaryVectorRecord.Vector), typeof(ReadOnlyMemory<float>), 1)
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

    private Task<SqliteCollection<Guid, SessionSummaryVectorRecord>> GetOrCreateCollectionAsync(
        string provider, int dimensions, CancellationToken ct) =>
        _collections.GetOrAdd(
            provider,
            p => new Lazy<Task<SqliteCollection<Guid, SessionSummaryVectorRecord>>>(() => CreateCollectionAsync(p, dimensions, ct))
        ).Value;

    private async Task<SqliteCollection<Guid, SessionSummaryVectorRecord>> CreateCollectionAsync(
        string provider, int dimensions, CancellationToken ct)
    {
        var collection = BuildCollection(provider, dimensions);
        await collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
        return collection;
    }

    private SqliteCollection<Guid, SessionSummaryVectorRecord> BuildCollection(string provider, int dimensions)
    {
        var definition = new VectorStoreCollectionDefinition
        {
            Properties =
            {
                new VectorStoreKeyProperty(nameof(SessionSummaryVectorRecord.ChunkId), typeof(Guid)),
                new VectorStoreDataProperty(nameof(SessionSummaryVectorRecord.ProjectId), typeof(Guid)) { IsIndexed = true },
                new VectorStoreDataProperty(nameof(SessionSummaryVectorRecord.SessionId), typeof(Guid)) { IsIndexed = true },
                new VectorStoreVectorProperty(nameof(SessionSummaryVectorRecord.Vector), typeof(ReadOnlyMemory<float>), dimensions)
                {
                    DistanceFunction = DistanceFunction.CosineDistance,
                },
            },
        };

        return new SqliteCollection<Guid, SessionSummaryVectorRecord>(
            _connectionString, CollectionNameFor(provider), new SqliteCollectionOptions { Definition = definition });
    }

    private static string CollectionNameFor(string provider)
    {
        var sanitized = new string(provider.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return $"session_summary_vectors_{(sanitized.Length > 0 ? sanitized : "default")}";
    }

    private sealed class SessionSummaryVectorRecord
    {
        public required Guid ChunkId { get; set; }
        public required Guid ProjectId { get; set; }
        public required Guid SessionId { get; set; }
        public required ReadOnlyMemory<float> Vector { get; set; }
    }
}
