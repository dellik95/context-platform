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
    /// <summary>Working directory Claude Code reported at SessionStart, captured once per session
    /// (not per-event -- it's constant for the session's lifetime) so summarization/memory has the
    /// project-relative context it never had before.</summary>
    public string? Cwd { get; set; }
    /// <summary>The SessionEnd hook's <c>reason</c> field (e.g. "clear", "logout", "prompt_input_exit"),
    /// captured verbatim instead of discarded.</summary>
    public string? EndReason { get; set; }
}
