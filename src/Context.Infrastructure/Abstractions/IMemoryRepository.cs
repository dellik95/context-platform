using Context.Domain.Entities;

namespace Context.Infrastructure.Abstractions;

public interface IMemoryRepository
{
    Task<Memory?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Memory> AddAsync(Memory memory, CancellationToken ct);
    Task UpdateAsync(Memory memory, CancellationToken ct);

    /// <summary>Hard delete — a user explicitly discarding a memory, not a supersession
    /// (superseded memories are kept for history via <see cref="Memory.SupersededById"/>).
    /// No-op (does not throw) if the id doesn't exist.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Simple substring match over Title/Body scoped to a project — the offline-friendly
    /// fallback path; a real implementation can layer embedding similarity on top via
    /// Context.Retrieval without changing this contract. When <paramref name="topic"/> is
    /// non-null/whitespace, results are additionally restricted to memories whose
    /// <see cref="Memory.Topic"/> matches it (case-insensitive, exact match) — lets a caller
    /// combine free-text search with "only this topic" the way the dashboard's filter bar does.</summary>
    Task<IReadOnlyList<Memory>> SearchAsync(Guid projectId, string query, bool includeSuperseded, CancellationToken ct, string? topic = null);

    Task<IReadOnlyList<Memory>> ListByProjectAsync(Guid projectId, bool includeSuperseded, CancellationToken ct);

    /// <summary>Batch fetch by id, e.g. to materialize hits from a vector-search pass
    /// (<c>IMemoryVectorIndex.SearchAsync</c>) that only returns ids/scores. Missing ids are
    /// silently omitted rather than erroring — a stale vector-index record pointing at a since-
    /// deleted memory should just drop out of the result set.</summary>
    Task<IReadOnlyList<Memory>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Points <paramref name="oldMemoryId"/> at <paramref name="newMemoryId"/> and nothing
    /// else. False when the row no longer exists.</summary>
    /// <remarks>Single-column update rather than read-modify-<see cref="UpdateAsync"/>: the latter
    /// writes back every column of a stale snapshot, reverting whatever else changed meanwhile.</remarks>
    Task<bool> MarkSupersededAsync(Guid oldMemoryId, Guid newMemoryId, CancellationToken ct);

    /// <summary>Points every id in <paramref name="oldMemoryIds"/> at <paramref name="newMemoryId"/>
    /// in one statement. Returns the number of rows updated; ids that no longer exist are ignored.</summary>
    /// <remarks>One statement rather than a loop over <see cref="MarkSupersededAsync"/> so a
    /// compaction merge can supersede a whole cluster inside the transaction that inserted the
    /// merged memory — a partial loop would otherwise leave some sources pointing at the merge and
    /// the rest still active, i.e. the same fact live twice.</remarks>
    Task<int> MarkSupersededBatchAsync(IReadOnlyCollection<Guid> oldMemoryIds, Guid newMemoryId, CancellationToken ct);

    /// <summary>Atomically increments <see cref="Memory.ReinforcementCount"/>, stamps
    /// <see cref="Memory.LastReinforcedAt"/>, and promotes the memory to
    /// <see cref="Domain.Enums.MemoryTier.LongTerm"/> at or above
    /// <paramref name="promotionReinforcementThreshold"/> (or when pinned). Returns the new count,
    /// or null when the row no longer exists.</summary>
    Task<int?> ReinforceAsync(Guid memoryId, DateTimeOffset reinforcedAt, int promotionReinforcementThreshold, CancellationToken ct);
}
