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

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        // MemoryConfiguration wires SupersededById as DeleteBehavior.Restrict, so a memory that
        // superseded this one (its SupersededById points AT this row) would block the delete with
        // an FK violation unless that pointer is cleared FIRST, not after.
        await db.Memories.Where(m => m.SupersededById == id).ExecuteUpdateAsync(
            setters => setters.SetProperty(m => m.SupersededById, (Guid?)null), ct);

        await db.Memories.Where(m => m.Id == id).ExecuteDeleteAsync(ct);
        // affected == 0 (id didn't exist) is a no-op, not an error — delete is idempotent here.
    }

    public async Task<IReadOnlyList<Memory>> SearchAsync(Guid projectId, string query, bool includeSuperseded, CancellationToken ct, string? topic = null)
    {
        var pattern = $"%{query}%";
        var q = db.Memories.AsNoTracking().Where(m => m.ProjectId == projectId &&
            (EF.Functions.Like(m.Title, pattern) || EF.Functions.Like(m.Body, pattern)));

        if (!includeSuperseded)
        {
            q = q.Where(m => m.SupersededById == null);
        }

        if (!string.IsNullOrWhiteSpace(topic))
        {
            // SQLite's LIKE is case-insensitive for ASCII by default (same assumption the
            // Title/Body match above relies on) — used here with no wildcards for an
            // effectively case-insensitive exact match against the topic tag.
            q = q.Where(m => m.Topic != null && EF.Functions.Like(m.Topic, topic));
        }

        // SQLite's EF Core provider cannot translate ORDER BY over DateTimeOffset columns
        // (LastReinforcedAt), so results are ordered client-side after filtering server-side.
        var results = await q.ToListAsync(ct);
        return [.. results.OrderByDescending(m => m.Pinned).ThenByDescending(m => m.LastReinforcedAt)];
    }

    public async Task<IReadOnlyList<Memory>> ListByProjectAsync(Guid projectId, bool includeSuperseded, CancellationToken ct)
    {
        var q = db.Memories.AsNoTracking().Where(m => m.ProjectId == projectId);
        if (!includeSuperseded)
        {
            q = q.Where(m => m.SupersededById == null);
        }

        var results = await q.ToListAsync(ct);
        return [.. results.OrderByDescending(m => m.Pinned).ThenByDescending(m => m.LastReinforcedAt)];
    }

    public async Task<IReadOnlyList<Memory>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        return await db.Memories.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync(ct);
    }
}
