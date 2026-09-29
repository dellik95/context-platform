using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Infrastructure.Abstractions;

/// <summary>
/// "Live" throughout means <see cref="MemoryStatus.Active"/> or <see cref="MemoryStatus.Contested"/>
/// (<see cref="Memory.IsLive"/>); Superseded and Obsolete memories are kept for history.
/// </summary>
public interface IMemoryRepository
{
    Task<Memory?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Memory> AddAsync(Memory memory, CancellationToken ct);
    Task UpdateAsync(Memory memory, CancellationToken ct);

    /// <summary>Hard delete of the memory with its evidence, entities and embedding. Memories it
    /// superseded lose their pointer and become Obsolete. No-op if the id doesn't exist.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Memory>> ListByProjectAsync(Guid projectId, bool includeSuperseded, CancellationToken ct);

    /// <summary>Live memories sharing <paramref name="topicKey"/> (exact match on the normalized key).</summary>
    Task<IReadOnlyList<Memory>> ListLiveByTopicKeyAsync(Guid projectId, string topicKey, CancellationToken ct);

    /// <summary>Batch fetch by id; missing ids are silently omitted.</summary>
    Task<IReadOnlyList<Memory>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    /// <summary>Appends evidence rows; empty ids are assigned.</summary>
    Task AddEvidenceAsync(IReadOnlyCollection<MemoryEvidence> evidence, CancellationToken ct);

    /// <summary>Evidence for the given memories, oldest first.</summary>
    Task<IReadOnlyList<MemoryEvidence>> ListEvidenceAsync(IReadOnlyCollection<Guid> memoryIds, CancellationToken ct);

    /// <summary>Replaces the full entity set of <paramref name="memoryId"/>; duplicate keys collapse.</summary>
    Task ReplaceEntitiesAsync(Guid memoryId, IReadOnlyCollection<MemoryEntity> entities, CancellationToken ct);

    Task<IReadOnlyList<MemoryEntity>> ListEntitiesAsync(IReadOnlyCollection<Guid> memoryIds, CancellationToken ct);

    /// <summary>Entity rows with any of <paramref name="keys"/> that belong to live memories of the project.</summary>
    Task<IReadOnlyList<MemoryEntity>> ListLiveEntitiesByKeysAsync(Guid projectId, IReadOnlyCollection<string> keys, CancellationToken ct);

    Task<MemoryConsolidationReport> AddReportAsync(MemoryConsolidationReport report, CancellationToken ct);
    Task<MemoryConsolidationReport?> GetReportAsync(Guid id, CancellationToken ct);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<MemoryConsolidationReport>> ListReportsAsync(Guid projectId, CancellationToken ct);

    /// <summary>Reports of every project in <paramref name="status"/>.</summary>
    Task<IReadOnlyList<MemoryConsolidationReport>> ListReportsByStatusAsync(ConsolidationReportStatus status, CancellationToken ct);

    /// <summary>Atomically moves a report from <paramref name="from"/> to <paramref name="to"/>, replacing its
    /// JSON when <paramref name="reportJson"/> is non-null. False when the report is missing or not in
    /// <paramref name="from"/>, so two callers can never both claim the same transition.</summary>
    Task<bool> SetReportStatusAsync(
        Guid id, ConsolidationReportStatus from, ConsolidationReportStatus to, DateTimeOffset? appliedAt, string? reportJson, CancellationToken ct);
}
