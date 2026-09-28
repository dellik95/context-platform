using Context.Domain.Entities;

namespace Context.Memory.Abstractions;

/// <summary>
/// Targeted memory reads and writes for consolidation, implemented by Context.Application so
/// Context.Memory stays free of EF Core. "Live" means Active or Contested.
/// </summary>
public interface IMemoryStore
{
    /// <summary>Runs <paramref name="work"/> in one write transaction that holds the database write lock
    /// from its first statement, so a memory read inside cannot change before the work commits. Nested
    /// calls join the outer transaction; vector-index writes are applied only after the commit.</summary>
    Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct);

    /// <summary>Missing ids are omitted.</summary>
    Task<IReadOnlyList<Context.Domain.Entities.Memory>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Entity rows with any of <paramref name="keys"/> belonging to live non-event memories of the project.</summary>
    Task<IReadOnlyList<MemoryEntity>> FindLiveKnowledgeEntityMatchesAsync(Guid projectId, IReadOnlyCollection<string> keys, CancellationToken ct);

    Task<IReadOnlyList<Context.Domain.Entities.Memory>> ListLiveByTopicKeyAsync(Guid projectId, string topicKey, CancellationToken ct);

    /// <summary>Nearest live memories to <paramref name="query"/> within its model's vector collection;
    /// Score is cosine similarity.</summary>
    Task<IReadOnlyList<(Guid MemoryId, double Score)>> SearchSimilarAsync(Guid projectId, MemoryVector query, int limit, CancellationToken ct);

    /// <summary>Stored embeddings by memory id; an unknown model key is returned as "".</summary>
    Task<IReadOnlyDictionary<Guid, MemoryVector>> GetEmbeddingsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    Task<IReadOnlyList<MemoryEntity>> ListEntitiesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Oldest first.</summary>
    Task<IReadOnlyList<MemoryEvidence>> ListEvidenceAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    Task<IReadOnlyList<Context.Domain.Entities.Memory>> ListLiveEventsBySessionAsync(Guid projectId, Guid sessionId, CancellationToken ct);

    /// <summary>Every live non-event memory of the project.</summary>
    Task<IReadOnlyList<Context.Domain.Entities.Memory>> ListLiveKnowledgeAsync(Guid projectId, CancellationToken ct);

    /// <summary>Inserts the memory with its entities and evidence in one save. A Superseded memory may be
    /// inserted directly (with SupersededById set). The vector is indexed only for a live memory.</summary>
    Task InsertAsync(
        Context.Domain.Entities.Memory memory,
        MemoryVector? embedding,
        IReadOnlyCollection<MemoryEntity> entities,
        IReadOnlyCollection<MemoryEvidence> evidence,
        CancellationToken ct);

    /// <summary>Writes every column of a live memory, appends <paramref name="evidence"/>, replaces the
    /// entity set when <paramref name="entities"/> is non-null and the embedding when
    /// <paramref name="reembedding"/> is non-null. Turning knowledge into an Event drops its embedding.
    /// The memory must have been read inside the caller's <see cref="InTransactionAsync{T}"/>; writing a
    /// memory that is not live, or over a stored row that is no longer live, throws.</summary>
    Task UpdateAsync(
        Context.Domain.Entities.Memory memory,
        MemoryVector? reembedding,
        IReadOnlyCollection<MemoryEntity>? entities,
        IReadOnlyCollection<MemoryEvidence> evidence,
        CancellationToken ct);

    /// <summary>Marks a live <paramref name="oldId"/> Superseded by <paramref name="newId"/> and drops its
    /// vector. False when the old memory is missing or no longer live.</summary>
    Task<bool> SupersedeAsync(Guid oldId, Guid newId, DateTimeOffset at, CancellationToken ct);

    /// <summary>Marks a live memory Obsolete and drops its vector. False when it is missing or no longer live.</summary>
    Task<bool> ObsoleteAsync(Guid id, CancellationToken ct);

    Task AddEvidenceAsync(IReadOnlyCollection<MemoryEvidence> evidence, CancellationToken ct);

    /// <summary>Replaces the stored embedding and, for a live memory, its vector-index record.</summary>
    Task UpsertEmbeddingAsync(Context.Domain.Entities.Memory memory, MemoryVector embedding, CancellationToken ct);
}
