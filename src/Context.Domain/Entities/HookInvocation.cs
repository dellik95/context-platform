using Context.Domain.Enums;

namespace Context.Domain.Entities;

/// <summary>A single call to one of the Claude Code lifecycle hook endpoints
/// (<c>Hooks/HookEndpoints.cs</c>), recorded for the Dashboard's live hook-activity view.</summary>
public class HookInvocation
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public HookType Type { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
