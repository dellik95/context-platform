using Context.Application.Abstractions;
using Context.Application.Support;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Application.Adapters;

/// <summary>
/// Implements Context.Memory's <see cref="IMemoryStore"/> over <see cref="IMemoryRepository"/> and
/// <see cref="ContextDbContext"/>. Each insert or update of a memory writes its entities, evidence and
/// embedding row in one SaveChanges. The sqlite-vec store writes through its own connection, so its
/// records are written after the EF write, and inside <see cref="InTransactionAsync{T}"/> only after
/// the commit (they would otherwise wait on this transaction's write lock). A failed vector write is
/// logged, not thrown: the committed rows stay authoritative, the index refills a missing record on its
/// next use, and readers filter a leftover one out against the live set.
/// </summary>
public sealed class MemoryStoreAdapter(
    IMemoryRepository memoryRepository,
    ContextDbContext db,
    IMemoryVectorIndex memoryVectorIndex,
    ILogger<MemoryStoreAdapter>? logger = null) : IMemoryStore
{
    private List<Func<CancellationToken, Task>>? _deferredVectorWrites;

    public async Task<T> InTransactionAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        if (_deferredVectorWrites is not null)
        {
            return await work().ConfigureAwait(false);
        }

        List<Func<CancellationToken, Task>> deferred = [];
        _deferredVectorWrites = deferred;
        T result;
        try
        {
            // Microsoft.Data.Sqlite opens this as BEGIN IMMEDIATE: concurrent writers queue behind it.
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            result = await work().ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _deferredVectorWrites = null;
        }

        // Committed rows are authoritative; mirror them into the vector index even if the caller cancels now.
        foreach (var write in deferred)
        {
            await TryWriteVectorAsync(write, CancellationToken.None).ConfigureAwait(false);
        }

        return result;
    }

    public Task<IReadOnlyList<DomainMemory>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        memoryRepository.GetByIdsAsync(ids, ct);

    public async Task<IReadOnlyList<MemoryEntity>> FindLiveKnowledgeEntityMatchesAsync(Guid projectId, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        var keyArray = keys.ToArray();
        var knowledgeIds = db.Memories
            .Where(m => m.ProjectId == projectId
                && m.Type != MemoryType.Event
                && (m.Status == MemoryStatus.Active || m.Status == MemoryStatus.Contested))
            .Select(m => m.Id);
        return await db.MemoryEntities.AsNoTracking()
            .Where(e => e.ProjectId == projectId && keyArray.Contains(e.Key) && knowledgeIds.Contains(e.MemoryId))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<DomainMemory>> ListLiveByTopicKeyAsync(Guid projectId, string topicKey, CancellationToken ct) =>
        memoryRepository.ListLiveByTopicKeyAsync(projectId, topicKey, ct);

    public Task<IReadOnlyList<(Guid MemoryId, double Score)>> SearchSimilarAsync(Guid projectId, MemoryVector query, int limit, CancellationToken ct) =>
        memoryVectorIndex.SearchAsync(projectId, query.ModelKey, query.Vector, limit, ct);

    public async Task<IReadOnlyDictionary<Guid, MemoryVector>> GetEmbeddingsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, MemoryVector>();
        }

        var idArray = ids.ToArray();
        var rows = await db.MemoryEmbeddings.AsNoTracking().Where(e => idArray.Contains(e.MemoryId)).ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(r => r.MemoryId, r => new MemoryVector(r.ModelKey ?? "", EmbeddingVectorCodec.Decode(r.Vector)));
    }

    public Task<IReadOnlyList<MemoryEntity>> ListEntitiesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        memoryRepository.ListEntitiesAsync(ids, ct);

    public Task<IReadOnlyList<MemoryEvidence>> ListEvidenceAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) =>
        memoryRepository.ListEvidenceAsync(ids, ct);

    public async Task<IReadOnlyList<DomainMemory>> ListLiveEventsBySessionAsync(Guid projectId, Guid sessionId, CancellationToken ct) =>
        await db.Memories.AsNoTracking()
            .Where(m => m.ProjectId == projectId
                && m.Type == MemoryType.Event
                && m.CreatedFromSessionId == sessionId
                && (m.Status == MemoryStatus.Active || m.Status == MemoryStatus.Contested))
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<DomainMemory>> ListLiveKnowledgeAsync(Guid projectId, CancellationToken ct) =>
        await db.Memories.AsNoTracking()
            .Where(m => m.ProjectId == projectId
                && m.Type != MemoryType.Event
                && (m.Status == MemoryStatus.Active || m.Status == MemoryStatus.Contested))
            .ToListAsync(ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<DomainMemory>> ListLiveKnowledgeCreatedSinceAsync(Guid projectId, DateTimeOffset since, CancellationToken ct)
    {
        // SQLite cannot compare DateTimeOffset server-side, so only ids and timestamps are pulled to filter.
        var stamps = await db.Memories.AsNoTracking()
            .Where(m => m.ProjectId == projectId
                && m.Type != MemoryType.Event
                && (m.Status == MemoryStatus.Active || m.Status == MemoryStatus.Contested))
            .Select(m => new { m.Id, m.CreatedAt })
            .ToListAsync(ct).ConfigureAwait(false);
        var ids = stamps.Where(s => s.CreatedAt >= since).Select(s => s.Id).ToArray();
        return ids.Length == 0 ? [] : await db.Memories.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync(ct).ConfigureAwait(false);
    }

    public async Task InsertAsync(
        DomainMemory memory,
        MemoryVector? embedding,
        IReadOnlyCollection<MemoryEntity> entities,
        IReadOnlyCollection<MemoryEvidence> evidence,
        CancellationToken ct)
    {
        var entityRows = EntityRows(memory, entities);
        var evidenceRows = WithIds(evidence);

        db.Memories.Add(memory);
        db.MemoryEntities.AddRange(entityRows);
        db.MemoryEvidence.AddRange(evidenceRows);
        var embeddingRow = embedding is null ? null : await StageEmbeddingAsync(memory.Id, embedding, ct).ConfigureAwait(false);

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Detach([memory, .. entityRows, .. evidenceRows, embeddingRow]);
        }

        if (embedding is not null && memory.IsLive)
        {
            await UpsertVectorAsync(memory, embedding.ModelKey, embedding.Vector, ct).ConfigureAwait(false);
        }
    }

    public async Task UpdateAsync(
        DomainMemory memory,
        MemoryVector? reembedding,
        IReadOnlyCollection<MemoryEntity>? entities,
        IReadOnlyCollection<MemoryEvidence> evidence,
        CancellationToken ct)
    {
        var stored = await db.Memories.FirstOrDefaultAsync(m => m.Id == memory.Id, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Memory {memory.Id} was not found.");
        if (!stored.IsLive || !memory.IsLive)
        {
            db.Entry(stored).State = EntityState.Detached;
            throw new InvalidOperationException(
                $"Memory {memory.Id} is {stored.Status} and cannot be written as {memory.Status}; only live memories are updated here.");
        }

        var topicChanged = !string.Equals(stored.Topic, memory.Topic, StringComparison.Ordinal);
        var becameEvent = stored.Type.IsKnowledge() && memory.Type.IsEvent();
        db.Entry(stored).CurrentValues.SetValues(memory);

        var touched = new List<object?> { stored };
        if (entities is not null)
        {
            touched.AddRange(await StageEntitySetAsync(memory, entities, ct).ConfigureAwait(false));
        }

        var evidenceRows = WithIds(evidence);
        db.MemoryEvidence.AddRange(evidenceRows);
        touched.AddRange(evidenceRows);

        var embeddingRow = becameEvent ? null
            : reembedding is null ? await db.MemoryEmbeddings.AsNoTracking().FirstOrDefaultAsync(e => e.MemoryId == memory.Id, ct).ConfigureAwait(false)
            : await StageEmbeddingAsync(memory.Id, reembedding, ct).ConfigureAwait(false);
        if (reembedding is not null && !becameEvent)
        {
            touched.Add(embeddingRow);
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Detach(touched);
        }

        if (becameEvent)
        {
            // Events are never embedded, so a reclassified memory leaves the vector index too.
            await db.MemoryEmbeddings.Where(e => e.MemoryId == memory.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await DeleteVectorAsync(memory.Id, ct).ConfigureAwait(false);
            return;
        }

        // The vector record carries Topic for filtered ANN, so a topic change re-upserts it.
        if (reembedding is not null)
        {
            await UpsertVectorAsync(memory, reembedding.ModelKey, reembedding.Vector, ct).ConfigureAwait(false);
        }
        else if (topicChanged && embeddingRow?.ModelKey is { } modelKey)
        {
            await UpsertVectorAsync(memory, modelKey, EmbeddingVectorCodec.Decode(embeddingRow.Vector), ct).ConfigureAwait(false);
        }
    }

    public async Task<bool> SupersedeAsync(Guid oldId, Guid newId, DateTimeOffset at, CancellationToken ct)
    {
        var changed = await LiveRow(oldId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(m => m.Status, MemoryStatus.Superseded)
                .SetProperty(m => m.SupersededById, newId)
                .SetProperty(m => m.SupersededAt, at), ct)
            .ConfigureAwait(false) > 0;
        if (changed)
        {
            await DeleteVectorAsync(oldId, ct).ConfigureAwait(false);
        }

        return changed;
    }

    public async Task<bool> ObsoleteAsync(Guid id, CancellationToken ct)
    {
        var changed = await LiveRow(id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.Status, MemoryStatus.Obsolete), ct)
            .ConfigureAwait(false) > 0;
        if (changed)
        {
            await DeleteVectorAsync(id, ct).ConfigureAwait(false);
        }

        return changed;
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Memories.AnyAsync(m => m.Id == id, ct).ConfigureAwait(false))
        {
            return false;
        }

        await memoryRepository.DeleteAsync(id, ct).ConfigureAwait(false);
        await DeleteVectorAsync(id, ct).ConfigureAwait(false);
        return true;
    }

    public Task AddEvidenceAsync(IReadOnlyCollection<MemoryEvidence> evidence, CancellationToken ct) =>
        memoryRepository.AddEvidenceAsync(evidence, ct);

    public async Task UpsertEmbeddingAsync(DomainMemory memory, MemoryVector embedding, CancellationToken ct)
    {
        var row = await StageEmbeddingAsync(memory.Id, embedding, ct).ConfigureAwait(false);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Detach([row]);
        }

        if (memory.IsLive)
        {
            await UpsertVectorAsync(memory, embedding.ModelKey, embedding.Vector, ct).ConfigureAwait(false);
        }
    }

    private Task UpsertVectorAsync(DomainMemory memory, string modelKey, float[] vector, CancellationToken ct)
    {
        var (id, projectId, topic) = (memory.Id, memory.ProjectId, memory.Topic);
        return WriteVectorAsync(token => memoryVectorIndex.UpsertAsync(id, projectId, topic, modelKey, vector, token), ct);
    }

    private Task DeleteVectorAsync(Guid id, CancellationToken ct) =>
        WriteVectorAsync(token => memoryVectorIndex.DeleteAsync([id], token), ct);

    private Task WriteVectorAsync(Func<CancellationToken, Task> write, CancellationToken ct)
    {
        if (_deferredVectorWrites is { } deferred)
        {
            deferred.Add(write);
            return Task.CompletedTask;
        }

        return TryWriteVectorAsync(write, ct);
    }

    private async Task TryWriteVectorAsync(Func<CancellationToken, Task> write, CancellationToken ct)
    {
        try
        {
            await write(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Memory vector index write failed after its rows were saved.");
        }
    }

    private IQueryable<DomainMemory> LiveRow(Guid id) =>
        db.Memories.Where(m => m.Id == id && (m.Status == MemoryStatus.Active || m.Status == MemoryStatus.Contested));

    private async Task<MemoryEmbedding> StageEmbeddingAsync(Guid memoryId, MemoryVector embedding, CancellationToken ct)
    {
        var packed = EmbeddingVectorCodec.Encode(embedding.Vector);
        var row = await db.MemoryEmbeddings.FirstOrDefaultAsync(e => e.MemoryId == memoryId, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new MemoryEmbedding { MemoryId = memoryId, Vector = packed, Dim = embedding.Vector.Length, ModelKey = embedding.ModelKey };
            db.MemoryEmbeddings.Add(row);
        }
        else
        {
            row.Vector = packed;
            row.Dim = embedding.Vector.Length;
            row.ModelKey = embedding.ModelKey;
        }

        return row;
    }

    /// <summary>Stages the difference between the stored entity set and <paramref name="entities"/>, so the
    /// replacement lands in the same SaveChanges as the memory itself.</summary>
    private async Task<List<MemoryEntity>> StageEntitySetAsync(DomainMemory memory, IReadOnlyCollection<MemoryEntity> entities, CancellationToken ct)
    {
        var existing = await db.MemoryEntities.Where(e => e.MemoryId == memory.Id).ToListAsync(ct).ConfigureAwait(false);
        var wanted = EntityRows(memory, entities).ToDictionary(e => e.Key, StringComparer.Ordinal);
        var touched = new List<MemoryEntity>(existing);

        foreach (var row in existing)
        {
            if (wanted.Remove(row.Key, out var replacement))
            {
                row.Display = replacement.Display;
            }
            else
            {
                db.MemoryEntities.Remove(row);
            }
        }

        db.MemoryEntities.AddRange(wanted.Values);
        touched.AddRange(wanted.Values);
        return touched;
    }

    private static List<MemoryEntity> EntityRows(DomainMemory memory, IReadOnlyCollection<MemoryEntity> entities) =>
        [.. entities
            .DistinctBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => new MemoryEntity { ProjectId = memory.ProjectId, MemoryId = memory.Id, Key = e.Key, Display = e.Display })];

    private static List<MemoryEvidence> WithIds(IReadOnlyCollection<MemoryEvidence> evidence)
    {
        foreach (var row in evidence.Where(e => e.Id == Guid.Empty))
        {
            row.Id = Guid.NewGuid();
        }

        return [.. evidence];
    }

    /// <summary>Written entities are detached so a later write of the same memory in this scope never
    /// collides with an instance still tracked from an earlier one.</summary>
    private void Detach(IEnumerable<object?> entities)
    {
        foreach (var entity in entities)
        {
            if (entity is not null)
            {
                db.Entry(entity).State = EntityState.Detached;
            }
        }
    }
}
