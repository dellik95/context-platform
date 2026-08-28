using System.Text.Json.Serialization;

namespace Context.Claude.Hooks;

/// <summary>Payload Claude Code sends to the <c>UserPromptSubmit</c> hook (stdin JSON).</summary>
public record UserPromptSubmitInput(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("prompt")] string Prompt);
