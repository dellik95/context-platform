using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Repositories;

public class HookInvocationRepository(ContextDbContext db) : IHookInvocationRepository
{
    public async Task<HookInvocation> RecordAsync(
        Guid id, Guid projectId, HookType type, string payloadJson, DateTimeOffset receivedAt, CancellationToken ct)
    {
        var invocation = new HookInvocation
        {
            Id = id,
            ProjectId = projectId,
            Type = type,
            PayloadJson = payloadJson,
            ReceivedAt = receivedAt,
        };

        db.HookInvocations.Add(invocation);
        await db.SaveChangesAsync(ct);
        return invocation;
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) =>
        db.HookInvocations.AsNoTracking().AnyAsync(h => h.Id == id, ct);

    public Task<int> CountAsync(Guid projectId, CancellationToken ct) =>
        db.HookInvocations.AsNoTracking().CountAsync(h => h.ProjectId == projectId, ct);

    public async Task<IReadOnlyList<HookInvocation>> GetRecentAsync(Guid projectId, int limit, CancellationToken ct)
    {
        // Same SQLite/DateTimeOffset ORDER BY limitation as SessionRepository.GetEventsAsync: the
        // (per-project, filtered) result is ordered client-side instead.
        var invocations = await db.HookInvocations.AsNoTracking()
            .Where(h => h.ProjectId == projectId)
            .ToListAsync(ct);

        return [.. invocations.OrderByDescending(h => h.ReceivedAt).Take(limit)];
    }

    public async Task DeleteOlderThanAsync(Guid projectId, DateTimeOffset upTo, CancellationToken ct)
    {
        // Same SQLite/DateTimeOffset ORDER BY-and-filter limitation as SessionRepository's
        // DeleteEventsUpToAsync: the cutoff is applied client-side rather than in the query.
        var invocations = await db.HookInvocations
            .Where(h => h.ProjectId == projectId)
            .ToListAsync(ct);

        var toDelete = invocations.Where(h => h.ReceivedAt <= upTo).ToList();
        if (toDelete.Count == 0)
        {
            return;
        }

        db.HookInvocations.RemoveRange(toDelete);
        await db.SaveChangesAsync(ct);
    }
}
