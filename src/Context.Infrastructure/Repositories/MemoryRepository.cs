using Context.Domain.Entities;
using Context.Domain.Enums;
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
        // Joins the caller's transaction when there is one.
        await using var tx = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;

        // SupersededById is Restrict, so the pointers at this row must go first.
        await db.Memories.Where(m => m.SupersededById == id).ExecuteUpdateAsync(setters => setters
            .SetProperty(m => m.SupersededById, (Guid?)null)
            .SetProperty(m => m.SupersededAt, (DateTimeOffset?)null)
            .SetProperty(m => m.Status, MemoryStatus.Obsolete), ct);

        await db.MemoryEvidence.Where(e => e.MemoryId == id).ExecuteDeleteAsync(ct);
        await db.MemoryEntities.Where(e => e.MemoryId == id).ExecuteDeleteAsync(ct);
        await db.MemoryEmbeddings.Where(e => e.MemoryId == id).ExecuteDeleteAsync(ct);
        await db.Memories.Where(m => m.Id == id).ExecuteDeleteAsync(ct);

        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }
    }

    public async Task<IReadOnlyList<Memory>> ListByProjectAsync(Guid projectId, bool includeSuperseded, CancellationToken ct)
    {
        var q = db.Memories.AsNoTracking().Where(m => m.ProjectId == projectId);
        if (!includeSuperseded)
        {
            q = LiveOnly(q);
        }

        return OrderForDisplay(await q.ToListAsync(ct));
    }

    public async Task<IReadOnlyList<Memory>> ListLiveByTopicKeyAsync(Guid projectId, string topicKey, CancellationToken ct)
    {
        var q = db.Memories.AsNoTracking().Where(m => m.ProjectId == projectId && m.TopicKey == topicKey);
        return OrderForDisplay(await LiveOnly(q).ToListAsync(ct));
    }

    public async Task<IReadOnlyList<Memory>> GetByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        return await db.Memories.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync(ct);
    }

    public async Task AddEvidenceAsync(IReadOnlyCollection<MemoryEvidence> evidence, CancellationToken ct)
    {
        if (evidence.Count == 0)
        {
            return;
        }

        foreach (var row in evidence)
        {
            if (row.Id == Guid.Empty)
            {
                row.Id = Guid.NewGuid();
            }
        }

        db.MemoryEvidence.AddRange(evidence);
        await db.SaveChangesAsync(ct);
        foreach (var row in evidence)
        {
            db.Entry(row).State = EntityState.Detached;
        }
    }

    public async Task<IReadOnlyList<MemoryEvidence>> ListEvidenceAsync(IReadOnlyCollection<Guid> memoryIds, CancellationToken ct)
    {
        if (memoryIds.Count == 0)
        {
            return [];
        }

        var ids = memoryIds.ToArray();
        var rows = await db.MemoryEvidence.AsNoTracking().Where(e => ids.Contains(e.MemoryId)).ToListAsync(ct);
        return [.. rows.OrderBy(e => e.ObservedAt)];
    }

    public async Task ReplaceEntitiesAsync(Guid memoryId, IReadOnlyCollection<MemoryEntity> entities, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        await db.MemoryEntities.Where(e => e.MemoryId == memoryId).ExecuteDeleteAsync(ct);

        var rows = entities
            .DistinctBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => new MemoryEntity { ProjectId = e.ProjectId, MemoryId = memoryId, Key = e.Key, Display = e.Display })
            .ToList();
        if (rows.Count > 0)
        {
            db.MemoryEntities.AddRange(rows);
            await db.SaveChangesAsync(ct);
            foreach (var row in rows)
            {
                db.Entry(row).State = EntityState.Detached;
            }
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryEntity>> ListEntitiesAsync(IReadOnlyCollection<Guid> memoryIds, CancellationToken ct)
    {
        if (memoryIds.Count == 0)
        {
            return [];
        }

        var ids = memoryIds.ToArray();
        return await db.MemoryEntities.AsNoTracking().Where(e => ids.Contains(e.MemoryId)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<MemoryEntity>> ListLiveEntitiesByKeysAsync(Guid projectId, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        var keyArray = keys.ToArray();
        var liveIds = LiveOnly(db.Memories.Where(m => m.ProjectId == projectId)).Select(m => m.Id);
        return await db.MemoryEntities.AsNoTracking()
            .Where(e => e.ProjectId == projectId && keyArray.Contains(e.Key) && liveIds.Contains(e.MemoryId))
            .ToListAsync(ct);
    }

    public async Task<MemoryConsolidationReport> AddReportAsync(MemoryConsolidationReport report, CancellationToken ct)
    {
        if (report.Id == Guid.Empty)
        {
            report.Id = Guid.NewGuid();
        }

        db.MemoryConsolidationReports.Add(report);
        await db.SaveChangesAsync(ct);
        db.Entry(report).State = EntityState.Detached;
        return report;
    }

    public Task<MemoryConsolidationReport?> GetReportAsync(Guid id, CancellationToken ct) =>
        db.MemoryConsolidationReports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<MemoryConsolidationReport>> ListReportsAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await db.MemoryConsolidationReports.AsNoTracking().Where(r => r.ProjectId == projectId).ToListAsync(ct);
        return [.. rows.OrderByDescending(r => r.CreatedAt)];
    }

    public async Task<IReadOnlyList<MemoryConsolidationReport>> ListReportsByStatusAsync(ConsolidationReportStatus status, CancellationToken ct) =>
        await db.MemoryConsolidationReports.AsNoTracking().Where(r => r.Status == status).ToListAsync(ct);

    public async Task<bool> SetReportStatusAsync(
        Guid id, ConsolidationReportStatus from, ConsolidationReportStatus to, DateTimeOffset? appliedAt, string? reportJson, CancellationToken ct)
    {
        var affected = await db.MemoryConsolidationReports
            .Where(r => r.Id == id && r.Status == from)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.Status, to)
                .SetProperty(r => r.AppliedAt, appliedAt)
                .SetProperty(r => r.ReportJson, r => reportJson ?? r.ReportJson), ct);

        return affected > 0;
    }

    private static IQueryable<Memory> LiveOnly(IQueryable<Memory> q) =>
        q.Where(m => m.Status == MemoryStatus.Active || m.Status == MemoryStatus.Contested);

    // SQLite's EF provider cannot ORDER BY DateTimeOffset columns, so ordering happens client-side.
    private static IReadOnlyList<Memory> OrderForDisplay(List<Memory> memories) =>
        [.. memories.OrderByDescending(m => m.Pinned).ThenByDescending(m => m.LastReinforcedAt)];
}
