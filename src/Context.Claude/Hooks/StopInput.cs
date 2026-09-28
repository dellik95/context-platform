using System.Text.Json.Serialization;

namespace Context.Claude.Hooks;

/// <summary>Payload Claude Code sends to the <c>Stop</c> hook (stdin JSON) when the main agent
/// finishes a turn.</summary>
public record StopInput(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("last_assistant_message")] string? LastAssistantMessage = null,
    [property: JsonPropertyName("stop_hook_active")] bool StopHookActive = false);
