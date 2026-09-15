using System.Text.Json.Serialization;

namespace Context.Claude.Hooks;

/// <summary>
/// Payload Claude Code sends to the <c>PostCompact</c> hook (stdin JSON).
/// <see cref="Trigger"/> is one of <c>"manual"</c> (user ran <c>/compact</c>) or
/// <c>"auto"</c> (context window auto-compaction).
/// </summary>
public record PostCompactInput(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("trigger")] string Trigger,
    [property: JsonPropertyName("cwd")] string? Cwd = null);
