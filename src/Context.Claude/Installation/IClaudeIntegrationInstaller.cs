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
    /// <paramref name="bearerToken"/> lets a caller that already minted a token elsewhere — the
    /// host's own <c>POST /api/projects</c>, which returns one — hand it in directly rather than
    /// this installer generating a second, unrelated one. When omitted, the
    /// existing offline behavior applies unchanged: reuse the token already on disk in this
    /// project's <c>context-platform.local.json</c>, or generate a fresh one if none exists yet.
    /// </summary>
    Task InstallAsync(Guid projectId, string projectRoot, CancellationToken ct, string? bearerToken = null);

    /// <summary>
    /// The narrow "just register MCP" path: merges only <c>.mcp.json</c> (the Context.Dashboard.Server MCP
    /// server entry) and writes only <c>.claude/context-platform.local.json</c> (the per-project bearer
    /// token + port). Nothing else — no hook scripts, no <c>.claude/settings.json</c> edits, no
    /// <c>.claude-plugin/plugin.json</c>, no skill doc, no <c>.gitignore</c> entries.
    /// <para/>
    /// This is what <c>POST /api/projects</c> calls automatically at project-creation time, so a
    /// freshly-created project's MCP tools work immediately without a separate manual step. It is
    /// deliberately NOT <see cref="InstallAsync"/>: that method is the full six-artifact install
    /// used by the explicit, user-initiated "Install Claude integration" action, and stays the
    /// method to call there. Use this one only when MCP registration alone is what's needed; use
    /// <see cref="InstallAsync"/> whenever the caller wants the complete integration (hooks included).
    /// <para/>
    /// Same token-resolution precedence as <see cref="InstallAsync"/>: reuse an existing token
    /// already on disk in <c>context-platform.local.json</c> if present, otherwise use
    /// <paramref name="bearerToken"/> if supplied, otherwise generate a fresh one. Same
    /// backup-before-overwrite convention (<c>.claude/.backup/&lt;timestamp&gt;/</c>) for any
    /// pre-existing file this touches.
    /// </summary>
    Task InstallMcpOnlyAsync(Guid projectId, string projectRoot, CancellationToken ct, string? bearerToken = null);

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
