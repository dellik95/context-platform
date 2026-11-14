using System.ComponentModel;
using Context.Application;
using Context.Dashboard.Server.Security;
using Context.Infrastructure.Abstractions;
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
        var matches = await sessionRepository
            .SearchForMcpAsync(currentProject.ProjectId, query, limit, ct)
            .ConfigureAwait(false);

        return [.. matches.Select(m => new SessionSummaryDto(
            m.Id, m.ClaudeSessionId, m.StartedAt, m.EndedAt, m.Status, m.SummaryTitle))];
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
