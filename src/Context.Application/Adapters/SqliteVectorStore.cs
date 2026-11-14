using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.Extensions.VectorData;
using Microsoft.SemanticKernel.Connectors.SqliteVec;

namespace Context.Application.Adapters;

/// <summary>
/// Shared per-provider sqlite-vec collection cache, ANN cosine search, table-sweep deletion,
/// and synchronous/asynchronous disposal used by chunk, memory, and session-summary vector indexes.
/// </summary>
internal sealed class SqliteVectorStore<TRecord>(
    string dbPath,
    string tableNamePrefix,
    Func<int, VectorStoreCollectionDefinition> buildDefinition,
    Func<TRecord, Guid> getKey,
    Func<string, SqliteCollection<Guid, TRecord>, int, CancellationToken, Task>? initializeCollection = null,
    Func<string, SqliteCollection<Guid, TRecord>, int, CancellationToken, Task>? onCollectionRetrieved = null)
    : IDisposable, IAsyncDisposable
    where TRecord : class
{
    private readonly ConcurrentDictionary<string, Lazy<Task<SqliteCollection<Guid, TRecord>>>> _collections = new();

    public string ConnectionString { get; } = $"Data Source={dbPath};Default Timeout=10";

    public string CollectionNameFor(string provider)
    {
        var sanitized = new string(provider.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return $"{tableNamePrefix}{(sanitized.Length > 0 ? sanitized : "default")}";
    }

    public async Task<SqliteCollection<Guid, TRecord>> GetOrCreateCollectionAsync(
        string provider, int dimensions, CancellationToken ct)
    {
        var lazy = _collections.GetOrAdd(
            provider,
            p => new Lazy<Task<SqliteCollection<Guid, TRecord>>>(
                () => CreateCollectionAsync(p, dimensions, CancellationToken.None)));

        SqliteCollection<Guid, TRecord> collection;
        try
        {
            collection = await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch when (lazy.IsValueCreated && (lazy.Value.IsFaulted || lazy.Value.IsCanceled))
        {
            // Never cache a failed initialization so transient SQLite lock/backfill errors recover.
            _collections.TryRemove(new KeyValuePair<string, Lazy<Task<SqliteCollection<Guid, TRecord>>>>(provider, lazy));
            throw;
        }

        if (onCollectionRetrieved is not null)
        {
            await onCollectionRetrieved(provider, collection, dimensions, ct).ConfigureAwait(false);
        }

        return collection;
    }

    public async Task<IReadOnlyList<(Guid Key, double Score)>> SearchAsync(
        string provider, float[] queryVector, int limit, Expression<Func<TRecord, bool>> filter, CancellationToken ct)
    {
        var collection = await GetOrCreateCollectionAsync(provider, queryVector.Length, ct).ConfigureAwait(false);
        var options = new VectorSearchOptions<TRecord> { Filter = filter };
        var results = new List<(Guid, double)>();

        await foreach (var hit in collection.SearchAsync(new ReadOnlyMemory<float>(queryVector), limit, options, ct)
            .ConfigureAwait(false))
        {
            // sqlite-vec CosineDistance is [0, 2] where 0 = identical; convert to [ -1, 1 ] higher-is-better.
            results.Add((getKey(hit.Record), 1.0 - (hit.Score ?? 1.0)));
        }

        return results;
    }

    public SqliteCollection<Guid, TRecord> OpenCollection(string tableName, int dimensions = 1) =>
        new(ConnectionString, tableName, new SqliteCollectionOptions { Definition = buildDefinition(dimensions) });

    public async Task DeleteByFilterAsync(Expression<Func<TRecord, bool>> filter, CancellationToken ct)
    {
        var tableNames = await SqliteVectorTables.ListAsync(ConnectionString, tableNamePrefix, ct).ConfigureAwait(false);
        foreach (var tableName in tableNames)
        {
            using var collection = OpenCollection(tableName);
            await collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);

            var ids = new List<Guid>();
            await foreach (var record in collection.GetAsync(filter, int.MaxValue, options: null, ct).ConfigureAwait(false))
            {
                ids.Add(getKey(record));
            }

            if (ids.Count > 0)
            {
                await collection.DeleteAsync(ids, ct).ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlySet<Guid>> ListKeysByFilterAsync(
        Expression<Func<TRecord, bool>> filter, CancellationToken ct, string? specificTableName = null)
    {
        var ids = new HashSet<Guid>();
        var tableNames = await SqliteVectorTables.ListAsync(ConnectionString, tableNamePrefix, ct).ConfigureAwait(false);
        IEnumerable<string> targets = specificTableName is null
            ? tableNames
            : tableNames.Where(t => string.Equals(t, specificTableName, StringComparison.Ordinal));

        foreach (var tableName in targets)
        {
            using var collection = OpenCollection(tableName);
            await collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
            await foreach (var record in collection.GetAsync(filter, int.MaxValue, options: null, ct).ConfigureAwait(false))
            {
                ids.Add(getKey(record));
            }
        }

        return ids;
    }

    public async Task DeleteKeysAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return;
        }

        var tableNames = await SqliteVectorTables.ListAsync(ConnectionString, tableNamePrefix, ct).ConfigureAwait(false);
        foreach (var tableName in tableNames)
        {
            using var collection = OpenCollection(tableName);
            await collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
            await collection.DeleteAsync(ids, ct).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        foreach (var lazy in _collections.Values)
        {
            if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully)
            {
                lazy.Value.GetAwaiter().GetResult().Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _collections.Values)
        {
            if (!lazy.IsValueCreated)
            {
                continue;
            }

            try
            {
                var collection = await lazy.Value.ConfigureAwait(false);
                collection.Dispose();
            }
            catch
            {
                // Faulted/canceled initializations hold no live connection to dispose.
            }
        }
    }

    private async Task<SqliteCollection<Guid, TRecord>> CreateCollectionAsync(
        string provider, int dimensions, CancellationToken ct)
    {
        var collection = OpenCollection(CollectionNameFor(provider), dimensions);
        try
        {
            if (initializeCollection is not null)
            {
                await initializeCollection(provider, collection, dimensions, ct).ConfigureAwait(false);
            }
            else
            {
                await collection.EnsureCollectionExistsAsync(ct).ConfigureAwait(false);
            }

            return collection;
        }
        catch
        {
            collection.Dispose();
            throw;
        }
    }
}
