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

    /// <summary>
    /// Writes <c>.claude/skills/context-memory/SKILL.md</c> (frontmatter: name: context-memory,
    /// description: one line) — a skill document teaching Claude Code to proactively call this
    /// project's registered MCP tools (context_search, code_search, symbol_search, memory_search,
    /// graph_impact, session_search — see §10/ContextMcpTools) for code lookup and persistent
    /// memory instead of re-deriving context from scratch. Same backup-before-overwrite convention
    /// as <see cref="InstallAsync"/> (via the same private BackupIfExists helper).
    /// </summary>
    Task InstallSkillAsync(Guid projectId, string projectRoot, CancellationToken ct);
}
