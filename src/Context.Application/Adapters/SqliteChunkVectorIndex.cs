using System.Collections.Concurrent;
using Context.Application.Abstractions;
using Context.Application.Support;
using Context.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace Context.Application.Adapters;

/// <summary>
/// <see cref="IChunkVectorIndex"/> backed by sqlite-vec (via <c>Microsoft.SemanticKernel.Connectors.SqliteVec</c>'s
/// <see cref="SqliteCollection{TKey,TRecord}"/>) -- a real ANN index in the same database file
/// <see cref="ContextDbContext"/> already uses, replacing what was previously a brute-force cosine
/// scan over every project chunk (see <see cref="SqliteSearchIndex"/>'s old implementation report).
/// </summary>
/// <remarks>
/// <b>Why keyed by provider, not by project:</b> vector dimensionality is fixed per embedding
/// provider/model (768 for Ollama's embeddinggemma, 1536 for OpenAI's text-embedding-3-small,
/// ...), and sqlite-vec's underlying <c>vec0</c> virtual table is created with a fixed dimension
/// count. One collection per provider (named <c>chunk_vectors_{provider}</c>) sidesteps ever
/// having to reconcile mismatched dimensions in one table; <c>ProjectId</c> is just an indexed
/// filter column within it, mirroring how the old <c>Embeddings</c> table already keys its
/// content-hash cache by <c>(ContentHash, Provider)</c> for the same reason.
///
/// <b>Record content is deliberately minimal</b> (just the key + the vector): a search result is
/// joined back to the live <c>Chunks</c>/<c>Files</c> tables at query time (see
/// <see cref="SqliteSearchIndex.VectorSearchAsync"/>) rather than denormalizing FileId/SymbolId/
/// snippet text into this record. That join also makes a stale record self-healing -- a chunk
/// that no longer exists (replaced on reindex, or its file deleted) simply drops out of the join
/// and is silently excluded from results, so there is no proactive delete-on-reindex step wired
/// up here. The tradeoff is that a project with a lot of reindex/delete churn slowly accumulates
/// orphaned rows in these collections; acceptable at this platform's local-first, single-project
/// scale (flagged as a known gap, not yet a problem -- same category as the old brute-force scan
/// it replaces).
///
/// <b>Lifetime:</b> singleton, for the same reason as <see cref="SqliteSearchIndex"/> and
/// <see cref="GraphPersistenceAdapter"/> -- it's consumed by singletons and therefore cannot
/// constructor-inject the pooled, Scoped <see cref="ContextDbContext"/> directly, so backfill
/// (see below) opens a short-lived context per call via <see cref="IDbContextFactory{TContext}"/>.
/// The realized <see cref="SqliteCollection{TKey,TRecord}"/> per provider is cached for the
/// process's lifetime; each one owns its own <c>Microsoft.Data.Sqlite.SqliteConnection</c> against
/// the same on-disk file EF Core uses, safe because <see cref="ContextDbContext"/> already runs in
/// WAL mode.
/// </remarks>
public sealed class SqliteChunkVectorIndex(string dbPath, IDbContextFactory<ContextDbContext> dbContextFactory)
    : IChunkVectorIndex, IDisposable
{
    // BACKLOG-07: this SqliteCollection owns its own Microsoft.Data.Sqlite connection, entirely
    // outside ContextDbContext.SqlitePragmaConnectionInterceptor, so the same 10s busy-wait can't be
    // applied as a literal `PRAGMA busy_timeout` (SqliteCollection gives us no hook to run one on its
    // internally-managed connection). Microsoft.Data.Sqlite has no busy_timeout connection-string
    // keyword either -- "Default Timeout" is its closest equivalent, driving the same managed
    // busy-retry loop natively PRAGMA busy_timeout would (see SQLite/Microsoft.Data.Sqlite docs);
    // 10 matches the interceptor's 10000ms so both connections back off for the same duration.
    private readonly string _connectionString = $"Data Source={dbPath};Default Timeout=10";
    private readonly ConcurrentDictionary<string, Lazy<Task<SqliteCollection<Guid, ChunkVectorRecord>>>> _collections = new();

    public async Task UpsertAsync(Guid chunkId, Guid projectId, string provider, float[] vector, CancellationToken ct)
    {
        var collection = await GetOrCreateCollectionAsync(provider, vector.Length, ct).ConfigureAwait(false);
        await collection.UpsertAsync(
            new ChunkVectorRecord { ChunkId = chunkId, ProjectId = projectId, Vector = vector },
            ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(Guid ChunkId, double Score)>> SearchAsync(
        Guid projectId, string provider, float[] queryVector, int limit, CancellationToken ct)
    {
        var collection = await GetOrCreateCollectionAsync(provider, queryVector.Length, ct).ConfigureAwait(false);

        var options = new VectorSearchOptions<ChunkVectorRecord> { Filter = r => r.ProjectId == projectId };
        var results = new List<(Guid, double)>();

        await foreach (var hit in collection.SearchAsync(new ReadOnlyMemory<float>(queryVector), limit, options, ct)
            .ConfigureAwait(false))
        {
            // The collection is configured with DistanceFunction.CosineDistance, so Score here is
            // [0, 2] cosine distance (0 = identical), not similarity -- verified live against a
            // throwaway probe (three known vectors, checked the returned Score against their
            // expected distances). Convert to keep IChunkVectorIndex's documented "higher is
            // better" contract, matching the brute-force scan it replaces.
            results.Add((hit.Record.ChunkId, 1.0 - (hit.Score ?? 1.0)));
        }

        return results;
    }

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

    private Task<SqliteCollection<Guid, ChunkVectorRecord>> GetOrCreateCollectionAsync(
        string provider, int dimensions, CancellationToken ct) =>
        _collections.GetOrAdd(
            provider,
            p => new Lazy<Task<SqliteCollection<Guid, ChunkVectorRecord>>>(() => CreateCollectionAsync(p, dimensions, ct))
        ).Value;

    private async Task<SqliteCollection<Guid, ChunkVectorRecord>> CreateCollectionAsync(
        string provider, int dimensions, CancellationToken ct)
    {
        var collection = BuildCollection(provider, dimensions);

        // A brand-new collection (this provider has never been searched/embedded into before in
        // this database) starts backfilled from whatever the old Embeddings content-hash cache
        // already has for it, so upgrading onto this index doesn't silently blank out search
        // results for a project that was already indexed before this feature existed.
        var existedBefore = await collection.CollectionExistsAsync(ct).ConfigureAwait(false);
        await collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
        if (!existedBefore)
        {
            await BackfillFromEmbeddingCacheAsync(collection, provider, ct).ConfigureAwait(false);
        }

        return collection;
    }

    private async Task BackfillFromEmbeddingCacheAsync(
        SqliteCollection<Guid, ChunkVectorRecord> collection, string provider, CancellationToken ct)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var rows = await (
            from c in db.Chunks.AsNoTracking()
            join f in db.Files.AsNoTracking() on c.FileId equals f.Id
            join e in db.Embeddings.AsNoTracking() on c.ContentHash equals e.ContentHash
            where e.Provider == provider && !f.IsDeleted
            select new { ChunkId = c.Id, f.ProjectId, e.Vector }
        ).ToListAsync(ct).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            return;
        }

        var records = rows.Select(r => new ChunkVectorRecord
        {
            ChunkId = r.ChunkId,
            ProjectId = r.ProjectId,
            Vector = new ReadOnlyMemory<float>(EmbeddingVectorCodec.Decode(r.Vector)),
        });

        await collection.UpsertAsync(records, ct).ConfigureAwait(false);
    }

    private SqliteCollection<Guid, ChunkVectorRecord> BuildCollection(string provider, int dimensions)
    {
        var definition = new VectorStoreCollectionDefinition
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

        return new SqliteCollection<Guid, ChunkVectorRecord>(
            _connectionString, CollectionNameFor(provider), new SqliteCollectionOptions { Definition = definition });
    }

    private static string CollectionNameFor(string provider)
    {
        var sanitized = new string(provider.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return $"chunk_vectors_{(sanitized.Length > 0 ? sanitized : "default")}";
    }

    private sealed class ChunkVectorRecord
    {
        public required Guid ChunkId { get; set; }
        public required Guid ProjectId { get; set; }
        public required ReadOnlyMemory<float> Vector { get; set; }
    }
}
