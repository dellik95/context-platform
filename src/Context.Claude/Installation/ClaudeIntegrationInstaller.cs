using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Context.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Context.Claude.Installation;

/// <summary>
/// Default <see cref="IClaudeIntegrationInstaller"/>. Writes/merges everything a project needs to
/// talk to Context.Dashboard.Server from inside Claude Code:
/// <list type="bullet">
/// <item><c>.claude/hooks/*.sh</c> and <c>.claude/hooks/*.ps1</c> — thin, fail-open hook scripts.</item>
/// <item><c>.claude/settings.json</c> — merged, not overwritten, to wire the lifecycle hooks.</item>
/// <item><c>.mcp.json</c> — merged, not overwritten, to register the Context.Dashboard.Server MCP server.</item>
/// <item><c>.claude-plugin/plugin.json</c> — the Context Platform plugin manifest.</item>
/// <item><c>.claude/context-platform.local.json</c> — a per-project bearer token + port, gitignored.</item>
/// </list>
/// Every pre-existing file this installer touches is copied first to
/// <c>.claude/.backup/&lt;UTC timestamp&gt;/&lt;original relative path&gt;</c>.
/// </summary>
public sealed class ClaudeIntegrationInstaller : IClaudeIntegrationInstaller
{
    private const string LocalConfigFileName = "context-platform.local.json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly ClaudeIntegrationOptions _options;
    private readonly ILogger<ClaudeIntegrationInstaller> _logger;

    public ClaudeIntegrationInstaller(
        IOptions<ClaudeIntegrationOptions> options,
        ILogger<ClaudeIntegrationInstaller>? logger = null)
    {
        _options = options.Value;
        _logger = logger ?? NullLogger<ClaudeIntegrationInstaller>.Instance;
    }

    public async Task InstallAsync(Guid projectId, string projectRoot, CancellationToken ct, string? bearerToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        projectRoot = Path.GetFullPath(projectRoot);
        Directory.CreateDirectory(projectRoot);

        var claudeDir = Path.Combine(projectRoot, ".claude");
        var hooksDir = Path.Combine(claudeDir, "hooks");
        var pluginDir = Path.Combine(projectRoot, ".claude-plugin");
        Directory.CreateDirectory(claudeDir);
        Directory.CreateDirectory(hooksDir);
        Directory.CreateDirectory(pluginDir);

        var backupRoot = Path.Combine(
            claudeDir, ".backup", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture));

        // Windows here means Claude Code (and its hook shell) is running on the machine being set up
        // right now — that's the best signal we have about which script flavor to wire into
        // settings.json at install time. Both .sh and .ps1 are always written for portability.
        var targetIsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        // 1) Hook scripts.
        foreach (var hook in HookDefinition.All)
        {
            var shPath = Path.Combine(hooksDir, $"{hook.FileBaseName}.sh");
            var ps1Path = Path.Combine(hooksDir, $"{hook.FileBaseName}.ps1");

            BackupIfExists(shPath, projectRoot, backupRoot);
            await WriteTextAsync(shPath, HookScriptGenerator.GenerateBash(hook, _options), ct).ConfigureAwait(false);
            TryMarkExecutable(shPath);

            BackupIfExists(ps1Path, projectRoot, backupRoot);
            await WriteTextAsync(ps1Path, HookScriptGenerator.GeneratePowerShell(hook, _options), ct).ConfigureAwait(false);
        }

        // 2) .claude/settings.json — merge hook wiring.
        var settingsPath = Path.Combine(claudeDir, "settings.json");
        BackupIfExists(settingsPath, projectRoot, backupRoot);
        var settings = await ReadOrCreateJsonObjectAsync(settingsPath, ct).ConfigureAwait(false);
        SettingsJsonMerger.MergeHooks(settings, HookDefinition.All, targetIsWindows);
        await WriteJsonAsync(settingsPath, settings, ct).ConfigureAwait(false);

        // Resolved up front (reusing an existing token so re-installing doesn't invalidate a
        // Context.Dashboard.Server that already has it cached/authorized) because step 3 below now needs it
        // too — /mcp requires the same bearer token as every other REST endpoint (BACKLOG-02).
        // A caller-supplied `bearerToken` (a shared Context.Dashboard.Server's POST /api/projects response)
        // always wins over both: that call is the only place this project's *new* raw token is
        // ever handed back, so reusing whatever was on disk here instead would silently keep
        // authenticating with a token the host never actually minted for it.
        var localConfigPath = Path.Combine(claudeDir, LocalConfigFileName);
        var resolvedToken = await ResolveTokenAsync(localConfigPath, bearerToken, ct).ConfigureAwait(false);

        // 3) .mcp.json — merge the Context.Dashboard.Server MCP server entry.
        await WriteMcpJsonAsync(projectRoot, backupRoot, resolvedToken, ct).ConfigureAwait(false);

        // 4) .claude-plugin/plugin.json manifest.
        var pluginPath = Path.Combine(pluginDir, "plugin.json");
        BackupIfExists(pluginPath, projectRoot, backupRoot);
        await WriteJsonAsync(pluginPath, BuildPluginManifest(), ct).ConfigureAwait(false);

        // 5) Bearer token + local config.
        await WriteLocalConfigAsync(projectId, localConfigPath, projectRoot, backupRoot, resolvedToken, ct).ConfigureAwait(false);

        // 6) .gitignore — make sure the local secret + data dir are never committed.
        await EnsureGitignoreEntriesAsync(projectRoot, backupRoot, ct).ConfigureAwait(false);

        List<string> written =
        [
            claudeDir, hooksDir, pluginDir, Path.GetDirectoryName(backupRoot)!,
            settingsPath, Path.Combine(projectRoot, ".mcp.json"), pluginPath, localConfigPath,
            Path.Combine(projectRoot, ".gitignore"),
        ];
        foreach (var hook in HookDefinition.All)
        {
            written.Add(Path.Combine(hooksDir, $"{hook.FileBaseName}.sh"));
            written.Add(Path.Combine(hooksDir, $"{hook.FileBaseName}.ps1"));
        }
        ProjectFileOwnership.MatchProjectRoot(projectRoot, written, [backupRoot], _logger);

        _logger.LogInformation(
            "Installed Claude Code integration for project {ProjectId} at {ProjectRoot}", projectId, projectRoot);
    }

    /// <summary>
    /// The narrow "just register MCP" path used automatically by <c>POST /api/projects</c> at
    /// project-creation time — see <see cref="IClaudeIntegrationInstaller.InstallMcpOnlyAsync"/>'s
    /// remarks for why this is deliberately not <see cref="InstallAsync"/>. Reuses exactly steps 3
    /// and 5 of <see cref="InstallAsync"/> above (<see cref="WriteMcpJsonAsync"/> and
    /// <see cref="WriteLocalConfigAsync"/>, via the same <see cref="ResolveTokenAsync"/> token
    /// precedence), plus the same backup-before-overwrite/directory-creation behavior those rely on.
    /// Deliberately skips hook scripts, <c>.claude/settings.json</c>, <c>.claude-plugin/plugin.json</c>,
    /// the skill doc, and <c>.gitignore</c> — none of those are created by this method.
    /// </summary>
    public async Task InstallMcpOnlyAsync(Guid projectId, string projectRoot, CancellationToken ct, string? bearerToken = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        projectRoot = Path.GetFullPath(projectRoot);
        Directory.CreateDirectory(projectRoot);

        var claudeDir = Path.Combine(projectRoot, ".claude");
        Directory.CreateDirectory(claudeDir);

        var backupRoot = Path.Combine(
            claudeDir, ".backup", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture));

        var localConfigPath = Path.Combine(claudeDir, LocalConfigFileName);
        var resolvedToken = await ResolveTokenAsync(localConfigPath, bearerToken, ct).ConfigureAwait(false);

        await WriteMcpJsonAsync(projectRoot, backupRoot, resolvedToken, ct).ConfigureAwait(false);
        await WriteLocalConfigAsync(projectId, localConfigPath, projectRoot, backupRoot, resolvedToken, ct).ConfigureAwait(false);

        ProjectFileOwnership.MatchProjectRoot(
            projectRoot,
            [claudeDir, Path.GetDirectoryName(backupRoot)!, Path.Combine(projectRoot, ".mcp.json"), localConfigPath],
            [backupRoot],
            _logger);

        _logger.LogInformation(
            "Registered MCP-only Claude Code integration for project {ProjectId} at {ProjectRoot}", projectId, projectRoot);
    }

    /// <summary>
    /// Token precedence shared by <see cref="InstallAsync"/> and <see cref="InstallMcpOnlyAsync"/>: a
    /// caller-supplied <paramref name="bearerToken"/> always wins (the one moment a *new* raw token
    /// is ever available), then whatever is already on disk in <c>context-platform.local.json</c>
    /// (so re-running either install doesn't invalidate a host that already has it), then a freshly
    /// generated one.
    /// </summary>
    private static async Task<string> ResolveTokenAsync(string localConfigPath, string? bearerToken, CancellationToken ct)
    {
        var existingToken = await TryReadExistingTokenAsync(localConfigPath, ct).ConfigureAwait(false);
        return bearerToken ?? existingToken ?? BearerTokenGenerator.Generate();
    }

    /// <summary>.mcp.json — merge the Context.Dashboard.Server MCP server entry. Shared by <see cref="InstallAsync"/>'s step 3 and <see cref="InstallMcpOnlyAsync"/>.</summary>
    private async Task WriteMcpJsonAsync(string projectRoot, string backupRoot, string resolvedToken, CancellationToken ct)
    {
        var mcpPath = Path.Combine(projectRoot, ".mcp.json");
        BackupIfExists(mcpPath, projectRoot, backupRoot);
        var mcpConfig = await ReadOrCreateJsonObjectAsync(mcpPath, ct).ConfigureAwait(false);
        var mcpUrl = $"http://127.0.0.1:{_options.HostPort}{_options.McpPath}";
        SettingsJsonMerger.MergeMcpServer(mcpConfig, mcpUrl, resolvedToken);
        await WriteSecretJsonAsync(mcpPath, mcpConfig, ct).ConfigureAwait(false);
    }

    /// <summary>Bearer token + local config file. Shared by <see cref="InstallAsync"/>'s step 5 and <see cref="InstallMcpOnlyAsync"/>.</summary>
    private async Task WriteLocalConfigAsync(
        Guid projectId, string localConfigPath, string projectRoot, string backupRoot, string resolvedToken, CancellationToken ct)
    {
        BackupIfExists(localConfigPath, projectRoot, backupRoot);
        var localConfig = new JsonObject
        {
            ["projectId"] = projectId.ToString(),
            ["port"] = _options.HostPort,
            ["bearerToken"] = resolvedToken,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        await WriteSecretJsonAsync(localConfigPath, localConfig, ct).ConfigureAwait(false);
    }

    public async Task InstallSkillAsync(Guid projectId, string projectRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        projectRoot = Path.GetFullPath(projectRoot);
        Directory.CreateDirectory(projectRoot);

        var claudeDir = Path.Combine(projectRoot, ".claude");
        var skillDir = Path.Combine(claudeDir, "skills", "context-memory");
        Directory.CreateDirectory(skillDir);

        var backupRoot = Path.Combine(
            claudeDir, ".backup", DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture));

        var skillPath = Path.Combine(skillDir, "SKILL.md");
        BackupIfExists(skillPath, projectRoot, backupRoot);
        await WriteTextAsync(skillPath, BuildSkillMarkdown(), ct).ConfigureAwait(false);

        ProjectFileOwnership.MatchProjectRoot(
            projectRoot,
            [claudeDir, Path.Combine(claudeDir, "skills"), skillDir, skillPath, Path.GetDirectoryName(backupRoot)!],
            [backupRoot],
            _logger);

        _logger.LogInformation(
            "Installed context-memory skill for project {ProjectId} at {ProjectRoot}", projectId, projectRoot);
    }

    private static string BuildSkillMarkdown() =>
        """
        ---
        name: context-memory
        description: Use this project's Context Platform MCP tools for code lookup and cross-session memory instead of re-deriving context from scratch.
        ---

        # Context Platform memory & code intelligence

        This project has Context Platform's MCP tools registered:

        - `context_search` - hybrid (lexical + semantic) search over code and docs, merged with ranked project memory into a token-budgeted context pack.
        - `code_search` - raw ranked code/text search hits, no memory merge or budgeting.
        - `symbol_search` - finds symbols (classes, functions, methods, ...) by name pattern.
        - `memory_search` - semantic search over the project's knowledge base: how the system works (architecture, component behavior, decisions and their reasons, constraints, configuration, gotchas, procedures, open issues).
        - `memory_get` - fetches one memory by id.
        - `memory_remember` - explicitly stores something right now, bypassing the normal extraction triggers.
        - `memory_forget` - permanently deletes one memory by id. No undo.
        - `graph_neighbors` - lists graph nodes reachable from a node within N hops, optionally filtered by edge kind.
        - `graph_path` - finds the shortest path between two graph nodes.
        - `graph_impact` - blast-radius analysis for a symbol: transitive callers, implementers, and covering tests.
        - `session_search` - lists recent Claude Code sessions for this project.
        - `session_get` - fetches one session's metadata, event log, and summary.
        - `project_info` - aggregate stats: files indexed, symbols indexed, memory count, last-indexed time.

        ## When to use them

        - Prefer `context_search` or `code_search` over re-reading files from scratch when looking for how something in the codebase works.
        - Check `memory_search` for how a component works, prior decisions, or known gotchas before re-investigating a question that may already be answered from an earlier session. Ask in plain language; a paraphrase finds the memory without sharing its words.
        - Call `graph_impact` on a symbol before changing it, to see its blast radius (callers, implementers, tests).

        ## Storing memories (`memory_remember`)

        Don't wait only for the user to literally say "remember that..." — the normal N-event/session-end
        extraction pipeline already catches routine work, so `memory_remember` exists for the moments that
        pipeline shouldn't have to wait for. Call it right away when, in the current exchange:

        - An explicit request to remember/note something is made ("remember that...", "note for later...",
          a standing instruction or preference).
        - A real architectural or design decision gets made (a library/approach/pattern chosen over
          alternatives, and why).
        - A non-obvious discovery surfaces about the codebase — a root cause, a gotcha, a behavior that
          would otherwise cost someone real time to re-find.
        - The user shares significant standing context you'd otherwise have to ask for again: a constraint,
          a deadline, who owns what, where something is tracked externally.
        - Work of real consequence completes and leaves durable knowledge behind: how the new or changed
          component works and why. Record that knowledge in the present tense, never the change itself —
          "Added X" / "Fixed Y" are change logs and do not belong in memory.

        Fill in `title`/`topic`/`facts`/`concepts`/`filesRead`/`filesModified` yourself when you call it —
        you already have the full context right now, so there's no reason to leave this an unstructured
        narrative dump the way a bare `content` string would. Be as selective here as the periodic
        extraction pipeline is: skip trivia, in-progress work with no conclusion yet, and anything that's
        just process narration with no durable outcome. Not every exchange needs a memory.

        ## Updating a memory

        There is no separate "update" tool. Call `memory_remember` again describing the corrected/current
        state — the consolidator dedups against existing memories on the same topic and reinforces or
        supersedes the old one automatically, preserving history rather than silently overwriting it.

        ## Deleting a memory (`memory_forget`)

        Reach for this only when a memory shouldn't exist at all, not merely when it's outdated (that's
        what re-remembering above is for): the user explicitly asks to forget/remove/delete something, or
        you find an existing memory (via `memory_search`/`memory_get`) that is now flatly wrong or
        describes an approach that's since been replaced, and leaving it would mislead a future session.
        It permanently deletes with no undo, so don't call it on a hunch.
        """;

    private JsonObject BuildPluginManifest() => new()
    {
        ["name"] = "context-platform",
        ["version"] = "0.1.0",
        ["description"] =
            "Persistent memory and code-intelligence context platform for Claude Code: cross-session " +
            "project memory, AST/code-graph navigation, and semantic search surfaced via MCP tools and " +
            "lifecycle hooks.",
        ["hookVersion"] = _options.HookVersion,
    };

    private static async Task<JsonObject> ReadOrCreateJsonObjectAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var node = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return node as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            // Already backed up by the caller before this read; start fresh rather than fail install.
            return new JsonObject();
        }
    }

    private static async Task<string?> TryReadExistingTokenAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var node = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var token = (node as JsonObject)?["bearerToken"]?.GetValue<string>();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch
        {
            return null;
        }
    }

    private static async Task WriteJsonAsync(string path, JsonNode node, CancellationToken ct)
    {
        var json = node.ToJsonString(WriteOptions);
        await File.WriteAllTextAsync(path, json + "\n", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// <see cref="WriteJsonAsync"/> for the two files that carry the raw bearer token
    /// (<c>.mcp.json</c> and <c>context-platform.local.json</c>). Unix creates them 0644, which
    /// hands every other account on the machine a credential with full API access to this project.
    /// </summary>
    private static async Task WriteSecretJsonAsync(string path, JsonNode node, CancellationToken ct)
    {
        await WriteJsonAsync(path, node, ct).ConfigureAwait(false);
        RestrictToOwner(path);
    }

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Best-effort: filesystems without POSIX modes (a WSL drvfs mount of an NTFS drive, a
            // CIFS share) reject the chmod. Failing the whole install over it would be worse.
        }
    }

    private static async Task WriteTextAsync(string path, string content, CancellationToken ct) =>
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), ct)
            .ConfigureAwait(false);

    private static void BackupIfExists(string path, string projectRoot, string backupRoot)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var relative = Path.GetRelativePath(projectRoot, path);
        var destination = Path.Combine(backupRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(path, destination, overwrite: true);

        // Backups of .mcp.json / context-platform.local.json contain the same token as the originals.
        RestrictToOwner(destination);
    }

    private static void TryMarkExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(
                path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch
        {
            // Best-effort only — Claude Code invokes scripts via `bash <path>`, so exec bit isn't required.
        }
    }

    private static async Task EnsureGitignoreEntriesAsync(string projectRoot, string backupRoot, CancellationToken ct)
    {
        string[] required = [".claude/context-platform.local.json", ".claude/hooks.log", ".context/"];
        var path = Path.Combine(projectRoot, ".gitignore");

        var existingLines = File.Exists(path)
            ? await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false)
            : [];

        var missing = required.Where(r => !existingLines.Any(l => l.Trim() == r)).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        BackupIfExists(path, projectRoot, backupRoot);

        var sb = new StringBuilder();
        foreach (var line in existingLines)
        {
            sb.Append(line).Append('\n');
        }

        if (existingLines.Length > 0 && existingLines[^1].Trim().Length != 0)
        {
            sb.Append('\n');
        }

        sb.Append("# Context Platform (added by context-platform installer)\n");
        foreach (var line in missing)
        {
            sb.Append(line).Append('\n');
        }

        await File.WriteAllTextAsync(path, sb.ToString(), ct).ConfigureAwait(false);
    }
}
