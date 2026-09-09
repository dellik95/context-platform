using System.Text.Json.Serialization;

namespace Context.Claude.Hooks;

/// <summary>
/// Payload Claude Code sends to the <c>SessionStart</c> hook (stdin JSON).
/// <see cref="Source"/> is one of <c>"startup"</c>, <c>"resume"</c>, or <c>"clear"</c>.
/// </summary>
public record SessionStartInput(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("cwd")] string? Cwd = null);
