using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Repositories;

public class HookInvocationRepository(ContextDbContext db) : IHookInvocationRepository
{
    public async Task<HookInvocation> RecordAsync(Guid projectId, HookType type, string payloadJson, CancellationToken ct)
    {
        var invocation = new HookInvocation
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Type = type,
            PayloadJson = payloadJson,
            ReceivedAt = DateTimeOffset.UtcNow,
        };

        db.HookInvocations.Add(invocation);
        await db.SaveChangesAsync(ct);
        return invocation;
    }

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
}
