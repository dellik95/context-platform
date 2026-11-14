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

    public async Task<IReadOnlyList<Chunk>> ListByProjectAsync(Guid projectId, CancellationToken ct) =>
        await db.Chunks.AsNoTracking()
            .Join(db.Files.AsNoTracking().Where(f => f.ProjectId == projectId && !f.IsDeleted), c => c.FileId, f => f.Id, (c, f) => c)
            .ToListAsync(ct);

    public async Task<IReadOnlySet<Guid>> ListIdsByProjectAsync(Guid projectId, CancellationToken ct) =>
        (await db.Chunks.AsNoTracking()
            .Join(db.Files.AsNoTracking().Where(f => f.ProjectId == projectId), c => c.FileId, f => f.Id, (c, f) => c.Id)
            .ToListAsync(ct)).ToHashSet();

    public async Task<IReadOnlyList<Guid>> ReplaceForFileAsync(Guid fileId, IReadOnlyList<Chunk> chunks, CancellationToken ct)
    {
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        var existing = await db.Chunks.Where(c => c.FileId == fileId).ToListAsync(ct);
        var removedIds = existing.Select(c => c.Id).ToList();
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
        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }

        return removedIds;
    }
}
