using Context.Application.Abstractions;
using Microsoft.Extensions.VectorData;

namespace Context.Application.Adapters;

/// <summary>
/// <see cref="ISessionSummaryVectorIndex"/> backed by sqlite-vec (<c>session_summary_vectors_{provider}</c>
/// per embedding provider). Delegates collection caching, search scoring, deletion sweeps, and
/// disposal to <see cref="SqliteVectorStore{TRecord}"/>.
/// </summary>
public sealed class SqliteSessionSummaryVectorIndex : ISessionSummaryVectorIndex, IDisposable, IAsyncDisposable
{
    private const string TableNamePrefix = "session_summary_vectors_";

    private readonly SqliteVectorStore<SessionSummaryVectorRecord> _store;

    public SqliteSessionSummaryVectorIndex(string dbPath)
    {
        _store = new SqliteVectorStore<SessionSummaryVectorRecord>(
            dbPath,
            TableNamePrefix,
            BuildDefinition,
            r => r.ChunkId);
    }

    public async Task UpsertAsync(Guid chunkId, Guid projectId, Guid sessionId, string provider, float[] vector, CancellationToken ct)
    {
        var collection = await _store.GetOrCreateCollectionAsync(provider, vector.Length, ct).ConfigureAwait(false);
        await collection.UpsertAsync(
            new SessionSummaryVectorRecord { ChunkId = chunkId, ProjectId = projectId, SessionId = sessionId, Vector = vector },
            ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<(Guid ChunkId, double Score)>> SearchAsync(
        Guid projectId, string provider, float[] queryVector, int limit, CancellationToken ct, Guid? sessionId = null) =>
        _store.SearchAsync(
            provider,
            queryVector,
            limit,
            sessionId is null
                ? r => r.ProjectId == projectId
                : r => r.ProjectId == projectId && r.SessionId == sessionId,
            ct);

    public Task DeleteByProjectAsync(Guid projectId, CancellationToken ct) =>
        _store.DeleteByFilterAsync(r => r.ProjectId == projectId, ct);

    public Task<IReadOnlySet<Guid>> ListChunkIdsAsync(Guid projectId, string provider, CancellationToken ct) =>
        _store.ListKeysByFilterAsync(r => r.ProjectId == projectId, ct, _store.CollectionNameFor(provider));

    public void Dispose() => _store.Dispose();

    public ValueTask DisposeAsync() => _store.DisposeAsync();

    private static VectorStoreCollectionDefinition BuildDefinition(int dimensions) => new()
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

    private sealed class SessionSummaryVectorRecord
    {
        public required Guid ChunkId { get; set; }
        public required Guid ProjectId { get; set; }
        public required Guid SessionId { get; set; }
        public required ReadOnlyMemory<float> Vector { get; set; }
    }
}
