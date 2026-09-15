using Context.Domain.Entities;

namespace Context.Infrastructure.Abstractions;

public interface ISessionRepository
{
    /// <summary>The session, or null if it doesn't exist or doesn't belong to <paramref name="projectId"/>.</summary>
    Task<Session?> GetByIdAsync(Guid projectId, Guid id, CancellationToken ct);

    /// <summary>Most recently started session with this <see cref="Session.ClaudeSessionId"/> within
    /// <paramref name="projectId"/>, or null if none exists yet. Used to dedupe: a Claude Code session id
    /// is stable for the lifetime of one Claude Code session, so a hook call for an id already seen should
    /// reuse that session's row instead of starting a new one (see
    /// <see cref="Context.Application.ContextEngine.StartSessionAsync"/>).</summary>
    Task<Session?> GetByClaudeSessionIdAsync(Guid projectId, string claudeSessionId, CancellationToken ct);

    Task<Session> AddAsync(Session session, CancellationToken ct);
    Task UpdateAsync(Session session, CancellationToken ct);

    Task AddEventAsync(SessionEvent sessionEvent, CancellationToken ct);

    /// <summary>Events for the session, or an empty list if the session doesn't exist or doesn't
    /// belong to <paramref name="projectId"/>.</summary>
    Task<IReadOnlyList<SessionEvent>> GetEventsAsync(Guid projectId, Guid sessionId, CancellationToken ct);

    /// <summary>Deletes every <see cref="SessionEvent"/> for <paramref name="sessionId"/> with
    /// <see cref="SessionEvent.Timestamp"/> at or before <paramref name="upTo"/> - used by
    /// <see cref="Context.Application.Jobs.Handlers.SummarizeSessionJobHandler"/> to clean up events
    /// already folded into a <see cref="SessionSummaryChunk"/>, once a project opts into
    /// <c>ProjectSettingsDto.DeleteSessionEventsAfterSummary</c>.</summary>
    Task DeleteEventsUpToAsync(Guid sessionId, DateTimeOffset upTo, CancellationToken ct);

    Task UpsertSummaryAsync(SessionSummary summary, CancellationToken ct);

    /// <summary>The session's summary, or null if it doesn't exist or the session doesn't belong to
    /// <paramref name="projectId"/>.</summary>
    Task<SessionSummary?> GetSummaryAsync(Guid projectId, Guid sessionId, CancellationToken ct);

    /// <summary>Inserts a new append-only <see cref="SessionSummaryChunk"/>. Assigns
    /// <see cref="SessionSummaryChunk.SequenceNumber"/> itself (max existing + 1 for the chunk's
    /// SessionId, or 1 if none exist yet) — callers don't need to know the next sequence number.</summary>
    Task<SessionSummaryChunk> AddSummaryChunkAsync(SessionSummaryChunk chunk, CancellationToken ct);

    /// <summary>All chunks for a session, ordered by <see cref="SessionSummaryChunk.SequenceNumber"/>
    /// ascending, or an empty list if the session doesn't exist or doesn't belong to
    /// <paramref name="projectId"/>.</summary>
    Task<IReadOnlyList<SessionSummaryChunk>> GetSummaryChunksAsync(Guid projectId, Guid sessionId, CancellationToken ct);

    /// <summary>A single chunk by id, or null if it doesn't exist or its session doesn't belong to
    /// <paramref name="projectId"/>. Used by
    /// <see cref="Context.Application.Jobs.Handlers.ExtractMemoryJobHandler"/> to load exactly the
    /// one chunk a given <c>ExtractMemory</c> job was enqueued for.</summary>
    Task<SessionSummaryChunk?> GetSummaryChunkByIdAsync(Guid projectId, Guid chunkId, CancellationToken ct);

    Task<IReadOnlyList<SessionListItem>> ListByProjectAsync(Guid projectId, int limit, CancellationToken ct);

    /// <summary>
    /// Lexical substring match (Title/Completed/Discovered/Decisions/OpenIssues/NextSteps) over
    /// <paramref name="projectId"/>'s session-summary chunks, newest first — the lexical half of
    /// <see cref="Context.Application.ContextEngine.SearchSessionSummariesAsync"/>'s hybrid search,
    /// mirroring <see cref="IMemoryRepository.SearchAsync"/>. <paramref name="sessionId"/> (optional)
    /// restricts the match to one session's own chunks.
    /// </summary>
    Task<IReadOnlyList<SessionSummaryChunk>> SearchSummaryChunksAsync(
        Guid projectId, string query, int limit, CancellationToken ct, Guid? sessionId = null);
}
