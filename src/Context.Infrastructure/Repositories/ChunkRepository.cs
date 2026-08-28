using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Repositories;

public class ChunkRepository(ContextDbContext db) : IChunkRepository
{
    public Task<Chunk?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Chunks.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<Chunk>> ListByFileAsync(Guid fileId, CancellationToken ct) =>
        await db.Chunks.AsNoTracking().Where(c => c.FileId == fileId).ToListAsync(ct);

    public async Task<IReadOnlyList<Chunk>> GetByContentHashesAsync(IEnumerable<string> contentHashes, CancellationToken ct)
    {
        var hashes = contentHashes.ToArray();
        return await db.Chunks.AsNoTracking().Where(c => hashes.Contains(c.ContentHash)).ToListAsync(ct);
    }

    public async Task ReplaceForFileAsync(Guid fileId, IReadOnlyList<Chunk> chunks, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var existing = await db.Chunks.Where(c => c.FileId == fileId).ToListAsync(ct);
        db.Chunks.RemoveRange(existing);

        foreach (var chunk in chunks)
        {
            if (chunk.Id == Guid.Empty)
            {
                chunk.Id = Guid.NewGuid();
            }

            chunk.FileId = fileId;
        }

        await db.Chunks.AddRangeAsync(chunks, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
