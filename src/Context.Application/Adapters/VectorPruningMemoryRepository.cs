using Context.Application.Abstractions;
using Context.Infrastructure.Abstractions;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Application.Adapters;

/// <summary>
/// Decorates <see cref="IMemoryRepository"/> so a hard-deleted or superseded memory takes its
/// sqlite-vec record with it. <c>MemoryEmbeddings</c> is cleaned up by its cascade foreign key; the
/// vector store has no foreign key back to <c>Memories</c>, so without this a forgotten memory
/// keeps competing for the ANN pass's top-K slots forever.
/// </summary>
/// <remarks>
/// Superseding prunes too, which is what makes deduplication and compaction worth running at all:
/// <c>ContextEngine.SearchMemoryAsync</c> drops superseded hits only <em>after</em> the ANN search
/// has already spent its fixed top-K on them, so a merged-away duplicate would go on displacing
/// live memories from semantic results forever. The cost is that <c>includeSuperseded: true</c>
/// reaches superseded memories through the substring pass alone; they are history, not knowledge
/// the project is still meant to retrieve by meaning.
/// </remarks>
/// <remarks>Same decorator reasoning as <see cref="VectorPruningChunkRepository"/>: both
/// <c>DELETE /api/memories/{id}</c> and the <c>memory_forget</c> MCP tool call the repository
/// directly, and neither should have to remember this.</remarks>
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

    public Task<IReadOnlyList<DomainMemory>> SearchAsync(Guid projectId, string query, bool includeSuperseded, CancellationToken ct, string? topic = null) =>
        inner.SearchAsync(projectId, query, includeSuperseded, ct, topic);

    public Task<IReadOnlyList<DomainMemory>> ListByProjectAsync(Guid projectId, bool includeSuperseded, CancellationToken ct) =>
        inner.ListByProjectAsync(projectId, includeSuperseded, ct);

    public Task<IReadOnlyList<DomainMemory>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => inner.GetByIdsAsync(ids, ct);

    public async Task<bool> MarkSupersededAsync(Guid oldMemoryId, Guid newMemoryId, CancellationToken ct)
    {
        if (!await inner.MarkSupersededAsync(oldMemoryId, newMemoryId, ct).ConfigureAwait(false))
        {
            return false;
        }

        await vectorIndex.DeleteAsync([oldMemoryId], ct).ConfigureAwait(false);
        return true;
    }

    /// <remarks>
    /// Unlike the single-id overload this does <em>not</em> prune the vectors, because its only
    /// caller (<see cref="MemoryStoreAdapter.SaveMergedAsync"/>) runs it inside an EF transaction:
    /// sqlite-vec holds its own connection, so writing through it here would either block on the
    /// write lock or, if the transaction then rolled back, delete the vectors of memories that are
    /// still live. That caller prunes after its commit instead.
    /// </remarks>
    public Task<int> MarkSupersededBatchAsync(IReadOnlyCollection<Guid> oldMemoryIds, Guid newMemoryId, CancellationToken ct) =>
        inner.MarkSupersededBatchAsync(oldMemoryIds, newMemoryId, ct);

    public Task<int?> ReinforceAsync(Guid memoryId, DateTimeOffset reinforcedAt, int promotionReinforcementThreshold, CancellationToken ct) =>
        inner.ReinforceAsync(memoryId, reinforcedAt, promotionReinforcementThreshold, ct);
}
