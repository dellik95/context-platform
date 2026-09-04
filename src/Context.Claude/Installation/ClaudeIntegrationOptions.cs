namespace Context.Claude.Installation;

/// <summary>
/// Configuration for generated Claude Code integration artifacts (hook scripts, <c>.mcp.json</c>
/// entry). Bind from configuration (e.g. section <c>"ClaudeIntegration"</c>) or set directly via
/// the <c>configure</c> delegate on <c>AddContextClaude</c>.
/// </summary>
public class ClaudeIntegrationOptions
{
    /// <summary>
    /// TCP port Context.Dashboard.Server listens on for hook callbacks
    /// (<c>http://127.0.0.1:{HostPort}/hooks/{name}</c>). Stored in the generated
    /// <c>.claude/context-platform.local.json</c> so it can change without regenerating scripts.
    /// </summary>
    public int HostPort { get; set; } = 47923;

    /// <summary>Hook contract version stamped into <c>ClaudeIntegration.HookVersion</c> and plugin.json.</summary>
    public string HookVersion { get; set; } = "0.1.0";

    /// <summary>
    /// Timeout (seconds) the generated hook scripts use when calling Context.Dashboard.Server. Kept short so a
    /// slow or unreachable Context Platform never meaningfully delays Claude Code — the scripts fail
    /// open (exit 0, no output) on any error, including timeout.
    /// </summary>
    public int HookTimeoutSeconds { get; set; } = 2;

    /// <summary>
    /// Path (relative to <see cref="HostPort"/>'s origin) that Context.Dashboard.Server maps its MCP endpoint
    /// to — see <c>app.MapMcp(...)</c> in <c>Context.Dashboard.Server/Program.cs</c>. The installer writes
    /// <c>.mcp.json</c> as an HTTP-type server entry (<c>http://127.0.0.1:{HostPort}{McpPath}</c>)
    /// rather than a spawned stdio command, because Context.Dashboard.Server already serves MCP over HTTP on
    /// this same always-running process (the spec's "one host process" decision) — there is no
    /// separate stdio server to launch.
    /// </summary>
    public string McpPath { get; set; } = "/mcp";
}
