using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Repositories;

public class SessionRepository(ContextDbContext db) : ISessionRepository
{
    public Task<Session?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<Session> AddAsync(Session session, CancellationToken ct)
    {
        if (session.Id == Guid.Empty)
        {
            session.Id = Guid.NewGuid();
        }

        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    public async Task UpdateAsync(Session session, CancellationToken ct)
    {
        db.Sessions.Update(session);
        await db.SaveChangesAsync(ct);
    }

    public async Task AddEventAsync(SessionEvent sessionEvent, CancellationToken ct)
    {
        if (sessionEvent.Id == Guid.Empty)
        {
            sessionEvent.Id = Guid.NewGuid();
        }

        db.SessionEvents.Add(sessionEvent);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SessionEvent>> GetEventsAsync(Guid sessionId, CancellationToken ct)
    {
        // SQLite's EF Core provider cannot translate ORDER BY over DateTimeOffset columns,
        // so the (bounded, per-session) result set is ordered client-side instead.
        var events = await db.SessionEvents.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .ToListAsync(ct);

        return [.. events.OrderBy(e => e.Timestamp)];
    }

    public async Task UpsertSummaryAsync(SessionSummary summary, CancellationToken ct)
    {
        var existing = await db.SessionSummaries.FirstOrDefaultAsync(s => s.SessionId == summary.SessionId, ct);
        if (existing is null)
        {
            db.SessionSummaries.Add(summary);
        }
        else
        {
            existing.Completed = summary.Completed;
            existing.Discovered = summary.Discovered;
            existing.Decisions = summary.Decisions;
            existing.ChangedFilesJson = summary.ChangedFilesJson;
            existing.OpenIssues = summary.OpenIssues;
            existing.NextSteps = summary.NextSteps;
            existing.GeneratedAt = summary.GeneratedAt;
        }

        await db.SaveChangesAsync(ct);
    }

    public Task<SessionSummary?> GetSummaryAsync(Guid sessionId, CancellationToken ct) =>
        db.SessionSummaries.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
}
