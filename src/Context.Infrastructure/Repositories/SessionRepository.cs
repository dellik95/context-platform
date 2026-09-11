using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Repositories;

public class SessionRepository(ContextDbContext db) : ISessionRepository
{
    public Task<Session?> GetByIdAsync(Guid projectId, Guid id, CancellationToken ct) =>
        db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.ProjectId == projectId, ct);

    public async Task<Session?> GetByClaudeSessionIdAsync(Guid projectId, string claudeSessionId, CancellationToken ct)
    {
        // Same SQLite/DateTimeOffset ORDER BY limitation as GetEventsAsync/ListByProjectAsync below:
        // this only ever matches a handful of rows (ideally one) per claudeSessionId, so sorting
        // client-side is cheap.
        var matches = await db.Sessions.AsNoTracking()
            .Where(s => s.ClaudeSessionId == claudeSessionId && s.ProjectId == projectId)
            .ToListAsync(ct);

        return matches.OrderByDescending(s => s.StartedAt).FirstOrDefault();
    }

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

    public async Task<IReadOnlyList<SessionEvent>> GetEventsAsync(Guid projectId, Guid sessionId, CancellationToken ct)
    {
        // SessionEvent has no ProjectId column of its own (only Session does), so ownership is
        // checked with a cheap existence probe first, then events are fetched by the
        // already-trusted sessionId — no join through Session on every event row.
        var owned = await db.Sessions.AsNoTracking()
            .AnyAsync(s => s.Id == sessionId && s.ProjectId == projectId, ct);
        if (!owned)
        {
            return [];
        }

        // SQLite's EF Core provider cannot translate ORDER BY over DateTimeOffset columns,
        // so the (bounded, per-session) result set is ordered client-side instead.
        var events = await db.SessionEvents.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .ToListAsync(ct);

        return [.. events.OrderBy(e => e.Timestamp)];
    }

    public async Task DeleteEventsUpToAsync(Guid sessionId, DateTimeOffset upTo, CancellationToken ct)
    {
        var toDelete = await db.SessionEvents
            .Where(e => e.SessionId == sessionId && e.Timestamp <= upTo)
            .ToListAsync(ct);

        if (toDelete.Count == 0)
        {
            return;
        }

        db.SessionEvents.RemoveRange(toDelete);
        await db.SaveChangesAsync(ct);
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
            existing.Title = summary.Title;
            existing.Completed = summary.Completed;
            existing.Discovered = summary.Discovered;
            existing.Decisions = summary.Decisions;
            existing.FilesReadJson = summary.FilesReadJson;
            existing.ChangedFilesJson = summary.ChangedFilesJson;
            existing.OpenIssues = summary.OpenIssues;
            existing.NextSteps = summary.NextSteps;
            existing.GeneratedAt = summary.GeneratedAt;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<SessionSummary?> GetSummaryAsync(Guid projectId, Guid sessionId, CancellationToken ct)
    {
        // Same ownership-check-then-fetch pattern as GetEventsAsync: SessionSummary has no
        // ProjectId column of its own, so verify via Session first rather than joining.
        var owned = await db.Sessions.AsNoTracking()
            .AnyAsync(s => s.Id == sessionId && s.ProjectId == projectId, ct);
        if (!owned)
        {
            return null;
        }

        return await db.SessionSummaries.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
    }

    public async Task<SessionSummaryChunk> AddSummaryChunkAsync(SessionSummaryChunk chunk, CancellationToken ct)
    {
        if (chunk.Id == Guid.Empty)
        {
            chunk.Id = Guid.NewGuid();
        }

        var maxSequence = await db.SessionSummaryChunks
            .Where(c => c.SessionId == chunk.SessionId)
            .Select(c => (int?)c.SequenceNumber)
            .MaxAsync(ct);

        chunk.SequenceNumber = (maxSequence ?? 0) + 1;

        db.SessionSummaryChunks.Add(chunk);
        await db.SaveChangesAsync(ct);
        return chunk;
    }

    public async Task<IReadOnlyList<SessionSummaryChunk>> GetSummaryChunksAsync(Guid projectId, Guid sessionId, CancellationToken ct)
    {
        // Same ownership-check-then-fetch pattern as GetEventsAsync: SessionSummaryChunk has no
        // ProjectId column of its own, so verify via Session first rather than joining.
        var owned = await db.Sessions.AsNoTracking()
            .AnyAsync(s => s.Id == sessionId && s.ProjectId == projectId, ct);
        if (!owned)
        {
            return [];
        }

        return await db.SessionSummaryChunks.AsNoTracking()
            .Where(c => c.SessionId == sessionId)
            .OrderBy(c => c.SequenceNumber)
            .ToListAsync(ct);
    }

    public async Task<SessionSummaryChunk?> GetSummaryChunkByIdAsync(Guid projectId, Guid chunkId, CancellationToken ct)
    {
        // Looked up by chunk id rather than session id, so the ownership check runs the other way
        // round: fetch the chunk first (cheap, single row), then verify its session belongs to
        // projectId.
        var chunk = await db.SessionSummaryChunks.AsNoTracking().FirstOrDefaultAsync(c => c.Id == chunkId, ct);
        if (chunk is null)
        {
            return null;
        }

        var owned = await db.Sessions.AsNoTracking()
            .AnyAsync(s => s.Id == chunk.SessionId && s.ProjectId == projectId, ct);
        return owned ? chunk : null;
    }

    public async Task<IReadOnlyList<SessionListItem>> ListByProjectAsync(Guid projectId, int limit, CancellationToken ct)
    {
        // Same SQLite/DateTimeOffset ORDER BY limitation as GetEventsAsync above: filter
        // server-side, sort the (bounded, per-project) result client-side.
        var sessions = await db.Sessions.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .ToListAsync(ct);

        var page = sessions.OrderByDescending(s => s.StartedAt).Take(limit).ToList();

        // SessionSummaries is one row per session (upserted in place), so a single extra
        // round-trip keyed on this page's session ids gets every title without an N+1 query.
        var pageIds = page.Select(s => s.Id).ToList();
        var titles = await db.SessionSummaries.AsNoTracking()
            .Where(sm => pageIds.Contains(sm.SessionId))
            .ToDictionaryAsync(sm => sm.SessionId, sm => sm.Title, ct);

        return [.. page.Select(s => new SessionListItem(s, titles.GetValueOrDefault(s.Id)))];
    }
}
