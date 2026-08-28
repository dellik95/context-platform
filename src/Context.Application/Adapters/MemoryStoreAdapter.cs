using Context.Application.Support;
using Context.Domain.Entities;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Application.Adapters;

/// <summary>
/// Adapts Context.Memory's <see cref="IMemoryStore"/> seam onto <see cref="IMemoryRepository"/>.
/// <see cref="Domain.Entities.MemoryEmbedding"/> has no repository interface of its own in
/// Context.Infrastructure (CONTRACTS.md §9 only calls out <c>IMemoryRepository</c>), so this
/// adapter talks to the injected <see cref="ContextDbContext"/> directly for that one entity —
/// the same pragmatic choice documented on <see cref="GraphPersistenceAdapter"/>.
/// </summary>
public sealed class MemoryStoreAdapter(IMemoryRepository memoryRepository, ContextDbContext db) : IMemoryStore
{
    public async Task<IReadOnlyList<ExistingMemoryWithEmbedding>> GetExistingWithEmbeddingsAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await (
            from m in db.Memories.AsNoTracking()
            join e in db.MemoryEmbeddings.AsNoTracking() on m.Id equals e.MemoryId
            where m.ProjectId == projectId && m.SupersededById == null
            select new { Memory = m, e.Vector }
        ).ToListAsync(ct).ConfigureAwait(false);

        return [.. rows.Select(r => new ExistingMemoryWithEmbedding(r.Memory, EmbeddingVectorCodec.Decode(r.Vector)))];
    }

    public async Task SaveAsync(Context.Domain.Entities.Memory memory, float[]? embedding, CancellationToken ct)
    {
        var existing = memory.Id != Guid.Empty
            ? await memoryRepository.GetByIdAsync(memory.Id, ct).ConfigureAwait(false)
            : null;

        if (existing is null)
        {
            await memoryRepository.AddAsync(memory, ct).ConfigureAwait(false);
        }
        else
        {
            await memoryRepository.UpdateAsync(memory, ct).ConfigureAwait(false);
        }

        if (embedding is null)
        {
            return;
        }

        var embeddingRow = await db.MemoryEmbeddings.FirstOrDefaultAsync(e => e.MemoryId == memory.Id, ct).ConfigureAwait(false);
        var packed = EmbeddingVectorCodec.Encode(embedding);

        if (embeddingRow is null)
        {
            db.MemoryEmbeddings.Add(new MemoryEmbedding { MemoryId = memory.Id, Vector = packed, Dim = embedding.Length });
        }
        else
        {
            embeddingRow.Vector = packed;
            embeddingRow.Dim = embedding.Length;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task MarkSupersededAsync(Guid oldMemoryId, Guid newMemoryId, CancellationToken ct)
    {
        var oldMemory = await memoryRepository.GetByIdAsync(oldMemoryId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Memory {oldMemoryId} was not found.");

        oldMemory.SupersededById = newMemoryId;
        await memoryRepository.UpdateAsync(oldMemory, ct).ConfigureAwait(false);
    }
}
