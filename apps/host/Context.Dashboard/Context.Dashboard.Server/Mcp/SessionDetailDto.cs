using Context.Domain.Entities;

namespace Context.Dashboard.Server.Mcp;

/// <summary>Full session detail returned by <see cref="SessionMcpTools.SessionGetAsync"/>.</summary>
public sealed record SessionDetailDto(
    Session Session,
    IReadOnlyList<SessionEvent> Events,
    SessionSummary? Summary,
    IReadOnlyList<SessionSummaryChunk> Chunks);
