namespace Context.Claude.Installation;

/// <summary>
/// Installs (or updates) the Claude Code integration for a project: hook scripts, a merged
/// <c>.claude/settings.json</c> and <c>.mcp.json</c>, a plugin manifest, and a local bearer-token
/// config file the hook scripts use to call back into Context.Host.
/// </summary>
public interface IClaudeIntegrationInstaller
{
    /// <summary>
    /// Safe-merges <c>.claude/settings.json</c> + <c>.mcp.json</c>, writes <c>hooks/*.sh</c> +
    /// <c>hooks/*.ps1</c>, and backs up any pre-existing files it touches under
    /// <c>.claude/.backup/&lt;timestamp&gt;/</c>.
    /// </summary>
    Task InstallAsync(Guid projectId, string projectRoot, CancellationToken ct);
}
