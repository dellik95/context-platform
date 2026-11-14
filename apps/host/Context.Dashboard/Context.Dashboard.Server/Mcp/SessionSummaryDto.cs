using Context.Domain.Enums;

namespace Context.Dashboard.Server.Mcp;

/// <summary>Compact per-session row returned by <see cref="SessionMcpTools.SessionSearchAsync"/>.</summary>
public sealed record SessionSummaryDto(
    Guid Id,
    string? ClaudeSessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    SessionStatus Status,
    string? Title);
