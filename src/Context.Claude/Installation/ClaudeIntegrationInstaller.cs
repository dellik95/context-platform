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
/// <item><c>.claude/settings.json</c> — merged, not overwritten, to wire the four hooks.</item>
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
        var existingToken = await TryReadExistingTokenAsync(localConfigPath, ct).ConfigureAwait(false);
        var resolvedToken = bearerToken ?? existingToken ?? BearerTokenGenerator.Generate();

        // 3) .mcp.json — merge the Context.Dashboard.Server MCP server entry.
        var mcpPath = Path.Combine(projectRoot, ".mcp.json");
        BackupIfExists(mcpPath, projectRoot, backupRoot);
        var mcpConfig = await ReadOrCreateJsonObjectAsync(mcpPath, ct).ConfigureAwait(false);
        var mcpUrl = $"http://127.0.0.1:{_options.HostPort}{_options.McpPath}";
        SettingsJsonMerger.MergeMcpServer(mcpConfig, mcpUrl, resolvedToken);
        await WriteJsonAsync(mcpPath, mcpConfig, ct).ConfigureAwait(false);

        // 4) .claude-plugin/plugin.json manifest.
        var pluginPath = Path.Combine(pluginDir, "plugin.json");
        BackupIfExists(pluginPath, projectRoot, backupRoot);
        await WriteJsonAsync(pluginPath, BuildPluginManifest(), ct).ConfigureAwait(false);

        // 5) Bearer token + local config.
        BackupIfExists(localConfigPath, projectRoot, backupRoot);
        var localConfig = new JsonObject
        {
            ["projectId"] = projectId.ToString(),
            ["port"] = _options.HostPort,
            ["bearerToken"] = resolvedToken,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        await WriteJsonAsync(localConfigPath, localConfig, ct).ConfigureAwait(false);

        // 6) .gitignore — make sure the local secret + data dir are never committed.
        await EnsureGitignoreEntriesAsync(projectRoot, backupRoot, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Installed Claude Code integration for project {ProjectId} at {ProjectRoot}", projectId, projectRoot);
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
        - `memory_search` - searches persistent memories (decisions, discoveries, bug fixes, open issues, ...).
        - `memory_get` - fetches one memory by id.
        - `graph_neighbors` - lists graph nodes reachable from a node within N hops, optionally filtered by edge kind.
        - `graph_path` - finds the shortest path between two graph nodes.
        - `graph_impact` - blast-radius analysis for a symbol: transitive callers, implementers, and covering tests.
        - `session_search` - lists recent Claude Code sessions for this project.
        - `session_get` - fetches one session's metadata, event log, and summary.
        - `project_info` - aggregate stats: files indexed, symbols indexed, memory count, last-indexed time.

        ## When to use them

        - Prefer `context_search` or `code_search` over re-reading files from scratch when looking for how something in the codebase works.
        - Check `memory_search` for prior decisions, bugfixes, or discoveries before re-investigating a question that may already be answered from an earlier session.
        - Call `graph_impact` on a symbol before changing it, to see its blast radius (callers, implementers, tests).
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
