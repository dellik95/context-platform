namespace Context.Claude.Installation;

/// <summary>
/// Configuration for generated Claude Code integration artifacts (hook scripts, <c>.mcp.json</c>
/// entry). Bind from configuration (e.g. section <c>"ClaudeIntegration"</c>) or set directly via
/// the <c>configure</c> delegate on <c>AddContextClaude</c>.
/// </summary>
public class ClaudeIntegrationOptions
{
    /// <summary>
    /// TCP port Context.Host listens on for hook callbacks
    /// (<c>http://127.0.0.1:{HostPort}/hooks/{name}</c>). Stored in the generated
    /// <c>.claude/context-platform.local.json</c> so it can change without regenerating scripts.
    /// </summary>
    public int HostPort { get; set; } = 47923;

    /// <summary>Hook contract version stamped into <c>ClaudeIntegration.HookVersion</c> and plugin.json.</summary>
    public string HookVersion { get; set; } = "0.1.0";

    /// <summary>
    /// Timeout (seconds) the generated hook scripts use when calling Context.Host. Kept short so a
    /// slow or unreachable Context Platform never meaningfully delays Claude Code — the scripts fail
    /// open (exit 0, no output) on any error, including timeout.
    /// </summary>
    public int HookTimeoutSeconds { get; set; } = 2;

    /// <summary>Command used to launch the Context.Host MCP stdio server from <c>.mcp.json</c>.</summary>
    public string McpCommand { get; set; } = "dotnet";

    /// <summary>
    /// Arguments passed to <see cref="McpCommand"/>. When <c>null</c>, the installer defaults to
    /// <c>["&lt;AppContext.BaseDirectory&gt;/Context.Host.dll", "--stdio"]</c> — i.e. it assumes
    /// Context.Host is deployed alongside whatever process runs the installer (Context.Cli).
    /// Override for custom deployment layouts (e.g. a published self-contained Context.Host).
    /// </summary>
    public string[]? McpArgs { get; set; }
}
