namespace Context.Claude.Installation;

/// <summary>
/// Describes one Claude Code lifecycle hook: its event name as used in <c>.claude/settings.json</c>,
/// the base filename used for its generated <c>.sh</c>/<c>.ps1</c> scripts, and the Context.Dashboard.Server
/// route segment (<c>/hooks/{HostRouteName}</c>) the scripts POST to.
/// </summary>
internal sealed record HookDefinition(string ClaudeEventName, string FileBaseName, string HostRouteName)
{
    public static readonly IReadOnlyList<HookDefinition> All =
    [
        new("SessionStart", "session-start", "session-start"),
        new("UserPromptSubmit", "user-prompt-submit", "user-prompt-submit"),
        new("PostToolUse", "post-tool-use", "post-tool-use"),
        new("SessionEnd", "session-end", "session-end"),
        new("PostCompact", "post-compact", "post-compact"),
        new("Stop", "stop", "stop"),
    ];
}
