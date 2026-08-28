using System.Text.Json.Serialization;

namespace Context.Claude.Hooks;

/// <summary>
/// Payload Claude Code sends to the <c>PostToolUse</c> hook (stdin JSON).
/// <see cref="ToolInputJson"/> and <see cref="ToolResponseJson"/> carry the tool's raw
/// (tool-specific) input/response JSON verbatim, as text, via <see cref="RawJsonStringConverter"/>.
/// </summary>
public record PostToolUseInput(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("tool_name")] string ToolName,
    [property: JsonPropertyName("tool_input"), JsonConverter(typeof(RawJsonStringConverter))]
    string ToolInputJson,
    [property: JsonPropertyName("tool_response"), JsonConverter(typeof(RawJsonStringConverter))]
    string ToolResponseJson);
