using System.ComponentModel;
using Context.Application;
using Context.Dashboard.Server.Security;
using Context.Domain.Enums;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace Context.Dashboard.Server.Mcp;

/// <summary>
/// MCP tools for browsing Claude Code sessions, retrieving session event/summary details,
/// and hybrid search across session summary checkpoints.
/// </summary>
[McpServerToolType]
public sealed class SessionMcpTools(
    IContextEngine engine,
    ISessionRepository sessionRepository,
    IDbContextFactory<ContextDbContext> dbContextFactory,
    CurrentProjectAccessor currentProject)
{
    private const int DefaultSessionSearchLimit = 20;

    [McpServerTool(Name = "session_search")]
    [Description("Lists recent Claude Code sessions for a project, optionally filtered by a substring of the Claude session id. " +
        "Excludes sessions with nothing worth surfacing: a session that ended but never got a summary generated for it, and a " +
        "session that consists only of tool calls with no actual user prompt in it.")]
    public async Task<IReadOnlyList<SessionSummaryDto>> SessionSearchAsync(
        [Description("Optional substring filter over the Claude session id.")] string? query = null,
        [Description("Maximum sessions to return.")] int limit = DefaultSessionSearchLimit,
        CancellationToken ct = default)
    {
        var pid = currentProject.ProjectId;
        await using var db = await dbContextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var sessions = db.Sessions.Where(s => s.ProjectId == pid);
        if (!string.IsNullOrWhiteSpace(query))
        {
            sessions = sessions.Where(s => s.ClaudeSessionId != null && s.ClaudeSessionId.Contains(query));
        }

        // Ended sessions without a summary or sessions without user prompts have no useful content to surface.
        sessions = sessions.Where(s =>
            s.Status != SessionStatus.Ended || db.SessionSummaries.Any(sm => sm.SessionId == s.Id));
        sessions = sessions.Where(s =>
            db.SessionEvents.Any(e => e.SessionId == s.Id && e.Type == SessionEventType.Prompt));

        return await sessions
            .OrderByDescending(s => s.StartedAt)
            .Take(Math.Max(1, limit))
            .Select(s => new SessionSummaryDto(
                s.Id, s.ClaudeSessionId, s.StartedAt, s.EndedAt, s.Status,
                db.SessionSummaries.Where(sm => sm.SessionId == s.Id).Select(sm => sm.Title).FirstOrDefault()))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    [McpServerTool(Name = "session_get")]
    [Description("Fetches one session's metadata, event log, and summary (if it has ended and been summarized), or null if it doesn't exist.")]
    public async Task<SessionDetailDto?> SessionGetAsync(
        [Description("Session id.")] Guid sessionId,
        CancellationToken ct = default)
    {
        var pid = currentProject.ProjectId;
        var session = await sessionRepository.GetByIdAsync(pid, sessionId, ct).ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        var events = await sessionRepository.GetEventsAsync(pid, sessionId, ct).ConfigureAwait(false);
        var summary = await sessionRepository.GetSummaryAsync(pid, sessionId, ct).ConfigureAwait(false);
        var chunks = await sessionRepository.GetSummaryChunksAsync(pid, sessionId, ct).ConfigureAwait(false);
        return new SessionDetailDto(session, events, summary, chunks);
    }

    [McpServerTool(Name = "session_summary_search")]
    [Description("Searches this project's session-summary chunks (per-session checkpoints covering completed work, discoveries, decisions, open issues, next steps) by hybrid lexical + semantic similarity — use for cross-session context (\"what did we decide about X\", \"when did we last touch Y\").")]
    public async Task<IReadOnlyList<Context.Domain.Entities.SessionSummaryChunk>> SessionSummarySearchAsync(
        [Description("Search query.")] string query,
        [Description("Optional session id to restrict the search to one session's chunks.")] Guid? sessionId = null,
        [Description("Maximum chunks to return.")] int limit = DefaultSessionSearchLimit,
        CancellationToken ct = default)
        => await engine.SearchSessionSummariesAsync(currentProject.ProjectId, query, ct, sessionId, limit).ConfigureAwait(false);
}
