using System.Text.Json.Serialization;

namespace Context.Claude.Hooks;

/// <summary>Payload Claude Code sends to the <c>SessionEnd</c> hook (stdin JSON).</summary>
public record SessionEndInput(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("reason")] string Reason);
