using Context.Application.Abstractions;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Application.Adapters;

/// <summary>
/// Decorates <see cref="IMemoryRepository"/> so a deleted memory takes its sqlite-vec record with it:
/// the vector store has no foreign key back to <c>Memories</c>. Status changes that leave the live set
/// prune through <see cref="MemoryStoreAdapter"/>.
/// </summary>
/// <remarks>
/// sqlite-vec writes through its own connection: never delete while an EF transaction is open on the
/// same database, or the vector write blocks on the write lock.
/// </remarks>
public sealed class VectorPruningMemoryRepository(IMemoryRepository inner, IMemoryVectorIndex vectorIndex) : IMemoryRepository
{
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        await inner.DeleteAsync(id, ct).ConfigureAwait(false);
        await vectorIndex.DeleteAsync([id], ct).ConfigureAwait(false);
    }

    public Task<DomainMemory?> GetByIdAsync(Guid id, CancellationToken ct) => inner.GetByIdAsync(id, ct);

    public Task<DomainMemory> AddAsync(DomainMemory memory, CancellationToken ct) => inner.AddAsync(memory, ct);

    public Task UpdateAsync(DomainMemory memory, CancellationToken ct) => inner.UpdateAsync(memory, ct);

    public Task<IReadOnlyList<DomainMemory>> ListByProjectAsync(Guid projectId, bool includeSuperseded, CancellationToken ct) =>
        inner.ListByProjectAsync(projectId, includeSuperseded, ct);

    public Task<IReadOnlyList<DomainMemory>> ListLiveByTopicKeyAsync(Guid projectId, string topicKey, CancellationToken ct) =>
        inner.ListLiveByTopicKeyAsync(projectId, topicKey, ct);

    public Task<IReadOnlyList<DomainMemory>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => inner.GetByIdsAsync(ids, ct);

    public Task AddEvidenceAsync(IReadOnlyCollection<MemoryEvidence> evidence, CancellationToken ct) => inner.AddEvidenceAsync(evidence, ct);

    public Task<IReadOnlyList<MemoryEvidence>> ListEvidenceAsync(IReadOnlyCollection<Guid> memoryIds, CancellationToken ct) =>
        inner.ListEvidenceAsync(memoryIds, ct);

    public Task ReplaceEntitiesAsync(Guid memoryId, IReadOnlyCollection<MemoryEntity> entities, CancellationToken ct) =>
        inner.ReplaceEntitiesAsync(memoryId, entities, ct);

    public Task<IReadOnlyList<MemoryEntity>> ListEntitiesAsync(IReadOnlyCollection<Guid> memoryIds, CancellationToken ct) =>
        inner.ListEntitiesAsync(memoryIds, ct);

    public Task<IReadOnlyList<MemoryEntity>> ListLiveEntitiesByKeysAsync(Guid projectId, IReadOnlyCollection<string> keys, CancellationToken ct) =>
        inner.ListLiveEntitiesByKeysAsync(projectId, keys, ct);

    public Task<MemoryConsolidationReport> AddReportAsync(MemoryConsolidationReport report, CancellationToken ct) => inner.AddReportAsync(report, ct);

    public Task<MemoryConsolidationReport?> GetReportAsync(Guid id, CancellationToken ct) => inner.GetReportAsync(id, ct);

    public Task<IReadOnlyList<MemoryConsolidationReport>> ListReportsAsync(Guid projectId, CancellationToken ct) => inner.ListReportsAsync(projectId, ct);

    public Task<bool> SetReportStatusAsync(
        Guid id, ConsolidationReportStatus from, ConsolidationReportStatus to, DateTimeOffset? appliedAt, string? reportJson, CancellationToken ct) =>
        inner.SetReportStatusAsync(id, from, to, appliedAt, reportJson, ct);
}
