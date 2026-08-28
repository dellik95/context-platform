using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Repositories;

public class MemoryRepository(ContextDbContext db) : IMemoryRepository
{
    public Task<Memory?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Memories.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<Memory> AddAsync(Memory memory, CancellationToken ct)
    {
        if (memory.Id == Guid.Empty)
        {
            memory.Id = Guid.NewGuid();
        }

        db.Memories.Add(memory);
        await db.SaveChangesAsync(ct);
        return memory;
    }

    public async Task UpdateAsync(Memory memory, CancellationToken ct)
    {
        db.Memories.Update(memory);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Memory>> SearchAsync(Guid projectId, string query, bool includeSuperseded, CancellationToken ct)
    {
        var pattern = $"%{query}%";
        var q = db.Memories.AsNoTracking().Where(m => m.ProjectId == projectId &&
            (EF.Functions.Like(m.Title, pattern) || EF.Functions.Like(m.Body, pattern)));

        if (!includeSuperseded)
        {
            q = q.Where(m => m.SupersededById == null);
        }

        return await q.OrderByDescending(m => m.Pinned)
            .ThenByDescending(m => m.LastReinforcedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Memory>> ListByProjectAsync(Guid projectId, bool includeSuperseded, CancellationToken ct)
    {
        var q = db.Memories.AsNoTracking().Where(m => m.ProjectId == projectId);
        if (!includeSuperseded)
        {
            q = q.Where(m => m.SupersededById == null);
        }

        return await q.OrderByDescending(m => m.Pinned)
            .ThenByDescending(m => m.LastReinforcedAt)
            .ToListAsync(ct);
    }
}
