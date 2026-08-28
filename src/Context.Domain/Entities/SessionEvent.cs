using Context.Domain.Enums;

namespace Context.Domain.Entities;

public class SessionEvent
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public SessionEventType Type { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public int? TokenEstimate { get; set; }
}
