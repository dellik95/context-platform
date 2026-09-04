using System.Text.Json.Serialization;

namespace Context.Claude.Hooks;

/// <summary>
/// Response body returned by Context.Dashboard.Server's <c>/hooks/{name}</c> endpoints to the thin generated
/// hook scripts. The scripts print <see cref="AdditionalContext"/> (when present) to stdout, which
/// Claude Code treats as additional context for <c>SessionStart</c>/<c>UserPromptSubmit</c> hooks.
/// <see cref="Block"/>/<see cref="BlockReason"/> are reserved for future blocking-hook scenarios
/// (e.g. PostToolUse policy checks) and are currently ignored by the generated scripts.
/// </summary>
public record HookOutput(
    [property: JsonPropertyName("additionalContext")] string? AdditionalContext,
    [property: JsonPropertyName("block")] bool Block = false,
    [property: JsonPropertyName("blockReason")] string? BlockReason = null);
