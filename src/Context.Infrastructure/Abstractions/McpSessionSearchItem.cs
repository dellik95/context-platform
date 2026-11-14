using Context.Domain.Enums;

namespace Context.Infrastructure.Abstractions;

public sealed record McpSessionSearchItem(
    Guid Id,
    string? ClaudeSessionId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    SessionStatus Status,
    string? SummaryTitle);
