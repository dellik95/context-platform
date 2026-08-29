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
        // GetByIdAsync is AsNoTracking, so within a single scope a caller can hold a Session
        // instance from a prior AddAsync/query that the context is *already* tracking under the
        // same key as the one passed here. Blindly calling Update(session) would then try to
        // attach a second instance with the same key and throw ("another instance with the same
        // key value is already being tracked"). Route through the existing tracked entry when
        // there is one; otherwise attach as usual.
        var tracked = db.ChangeTracker.Entries<Session>().FirstOrDefault(e => e.Entity.Id == session.Id);
        if (tracked is not null)
        {
            tracked.CurrentValues.SetValues(session);
        }
        else
        {
            db.Sessions.Update(session);
        }

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

    public async Task<IReadOnlyList<Session>> ListByProjectAsync(Guid projectId, int limit, CancellationToken ct)
    {
        // Same SQLite/DateTimeOffset ORDER BY limitation as GetEventsAsync above: filter
        // server-side, sort the (bounded, per-project) result client-side.
        var sessions = await db.Sessions.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .ToListAsync(ct);

        return [.. sessions.OrderByDescending(s => s.StartedAt).Take(limit)];
    }
}
