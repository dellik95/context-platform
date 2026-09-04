namespace Context.Claude.Installation;

/// <summary>
/// Installs (or updates) the Claude Code integration for a project: hook scripts, a merged
/// <c>.claude/settings.json</c> and <c>.mcp.json</c>, a plugin manifest, and a local bearer-token
/// config file the hook scripts use to call back into Context.Dashboard.Server.
/// </summary>
public interface IClaudeIntegrationInstaller
{
    /// <summary>
    /// Safe-merges <c>.claude/settings.json</c> + <c>.mcp.json</c>, writes <c>hooks/*.sh</c> +
    /// <c>hooks/*.ps1</c>, and backs up any pre-existing files it touches under
    /// <c>.claude/.backup/&lt;timestamp&gt;/</c>.
    /// <para/>
    /// <paramref name="bearerToken"/> lets a caller that already minted a token elsewhere — e.g.
    /// Context.Cli, after a shared Context.Dashboard.Server's <c>POST /api/projects</c> returned one — hand it
    /// in directly rather than this installer generating a second, unrelated one. When omitted, the
    /// existing offline behavior applies unchanged: reuse the token already on disk in this
    /// project's <c>context-platform.local.json</c>, or generate a fresh one if none exists yet.
    /// </summary>
    Task InstallAsync(Guid projectId, string projectRoot, CancellationToken ct, string? bearerToken = null);

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
