using Context.Domain.Enums;

namespace Context.Domain.Entities;

public class Session
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string? ClaudeSessionId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public SessionStatus Status { get; set; }
}
