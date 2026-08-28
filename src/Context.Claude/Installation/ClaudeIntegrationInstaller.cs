using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Context.Claude.Installation;

/// <summary>
/// Default <see cref="IClaudeIntegrationInstaller"/>. Writes/merges everything a project needs to
/// talk to Context.Host from inside Claude Code:
/// <list type="bullet">
/// <item><c>.claude/hooks/*.sh</c> and <c>.claude/hooks/*.ps1</c> — thin, fail-open hook scripts.</item>
/// <item><c>.claude/settings.json</c> — merged, not overwritten, to wire the four hooks.</item>
/// <item><c>.mcp.json</c> — merged, not overwritten, to register the Context.Host MCP server.</item>
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

    public async Task InstallAsync(Guid projectId, string projectRoot, CancellationToken ct)
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

        // 3) .mcp.json — merge the Context.Host MCP server entry.
        var mcpPath = Path.Combine(projectRoot, ".mcp.json");
        BackupIfExists(mcpPath, projectRoot, backupRoot);
        var mcpConfig = await ReadOrCreateJsonObjectAsync(mcpPath, ct).ConfigureAwait(false);
        SettingsJsonMerger.MergeMcpServer(mcpConfig, _options.McpCommand, _options.McpArgs ?? DefaultMcpArgs());
        await WriteJsonAsync(mcpPath, mcpConfig, ct).ConfigureAwait(false);

        // 4) .claude-plugin/plugin.json manifest.
        var pluginPath = Path.Combine(pluginDir, "plugin.json");
        BackupIfExists(pluginPath, projectRoot, backupRoot);
        await WriteJsonAsync(pluginPath, BuildPluginManifest(), ct).ConfigureAwait(false);

        // 5) Bearer token + local config (reuse an existing token so re-installing doesn't
        //    invalidate a Context.Host that already has it cached/authorized).
        var localConfigPath = Path.Combine(claudeDir, LocalConfigFileName);
        var existingToken = await TryReadExistingTokenAsync(localConfigPath, ct).ConfigureAwait(false);
        BackupIfExists(localConfigPath, projectRoot, backupRoot);
        var localConfig = new JsonObject
        {
            ["projectId"] = projectId.ToString(),
            ["port"] = _options.HostPort,
            ["bearerToken"] = existingToken ?? GenerateBearerToken(),
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("O"),
        };
        await WriteJsonAsync(localConfigPath, localConfig, ct).ConfigureAwait(false);

        // 6) .gitignore — make sure the local secret + data dir are never committed.
        await EnsureGitignoreEntriesAsync(projectRoot, backupRoot, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Installed Claude Code integration for project {ProjectId} at {ProjectRoot}", projectId, projectRoot);
    }

    private static string[] DefaultMcpArgs() =>
        [Path.Combine(AppContext.BaseDirectory, "Context.Host.dll"), "--stdio"];

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

    private static string GenerateBearerToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static async Task EnsureGitignoreEntriesAsync(string projectRoot, string backupRoot, CancellationToken ct)
    {
        string[] required = [".claude/context-platform.local.json", ".context/"];
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
