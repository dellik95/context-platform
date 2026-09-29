using Context.Shared;

namespace Context.Dashboard.Server;

/// <summary>
/// Resolved startup configuration for this Context.Dashboard.Server process, computed once in
/// <c>Program.cs</c> from environment variables (with safe defaults so the host runs with zero
/// configuration):
/// <list type="bullet">
/// <item><c>CONTEXT_PLATFORM_ROOT</c> — the single project root this process serves (defaults to
/// the current working directory). Still used for indexing/file-watching and everything else that
/// is genuinely per-project — just no longer for where the database lives, see
/// <see cref="ProjectDataDir"/>.</item>
/// <item><c>CONTEXT_PLATFORM_DATA_DIR</c> (or an OS-appropriate default) — see
/// <see cref="SharedDataLocation"/>, which resolves <see cref="ProjectDataDir"/>/<see cref="DbPath"/>.</item>
/// <item><c>CONTEXT_PLATFORM_PORT</c> — the port Kestrel binds to (defaults to
/// <see cref="Context.Claude.Installation.ClaudeIntegrationOptions.HostPort"/>'s own default,
/// 47923, so a project installed via <c>context claude install</c> talks to this host with zero
/// extra configuration).</item>
/// <item><c>CONTEXT_PLATFORM_BIND_HOST</c> — the address Kestrel binds to. Defaults to
/// <c>127.0.0.1</c> (loopback-only, matching the spec's local-first security posture) UNLESS
/// <c>DOTNET_RUNNING_IN_CONTAINER</c> is set (the official .NET Docker base images set this
/// automatically), in which case it defaults to <c>0.0.0.0</c> — inside a container "loopback"
/// means "unreachable from outside the container", so the actual access boundary shifts to how
/// the container's port is published (see deploy/docker/README.md: publish it bound to the host's
/// own 127.0.0.1, e.g. <c>-p 127.0.0.1:47923:47923</c>, to keep the same effective guarantee).</item>
/// <item><c>CONTEXT_PLATFORM_REDIS</c> — optional. When set, this process's <c>IJobSignal</c> is
/// Redis pub/sub-backed instead of the single-process in-memory default, so more than one
/// <c>Context.Dashboard.Server</c> instance can share one job queue (see Program.cs's registration
/// and <c>Jobs/RedisJobSignal.cs</c>). Unset means single-instance operation, unchanged from
/// before this existed.</item>
/// <item><c>CONTEXT_PLATFORM_JOB_WORKERS_PER_PROJECT</c> / <c>CONTEXT_PLATFORM_JOB_SHARED_WORKERS</c>
/// — optional. Job slots reserved for each project lane, and host-wide slots busy lanes borrow on
/// top (see <c>JobDispatcherOptions</c>); read directly in Program.cs.</item>
/// </list>
/// </summary>
public sealed class ContextHostOptions
{
    /// <summary>Default port, matching <c>ClaudeIntegrationOptions.HostPort</c>'s own default.</summary>
    public const int DefaultPort = 47923;

    /// <summary>The single project this host process serves — indexing root, file watcher, etc.
    /// Deliberately NOT where the database lives any more; see <see cref="ProjectDataDir"/>.</summary>
    public required string ProjectRoot { get; init; }

    /// <summary>
    /// The shared, machine-level data directory resolved by <see cref="SharedDataLocation.ResolveDataDir"/>
    /// — database, logs, and backups all live under here. No longer derived from
    /// <see cref="ProjectRoot"/> (Phase 2: the database moved out of any one project's repo so a
    /// single host process can eventually serve multiple projects against one database).
    /// </summary>
    public required string ProjectDataDir { get; init; }

    /// <summary><c>{ProjectDataDir}/context.db</c>, via <see cref="SharedDataLocation.ResolveDbPath"/>.</summary>
    public required string DbPath { get; init; }

    public required int Port { get; init; }

    public required string BindHost { get; init; }

    /// <summary><c>CONTEXT_PLATFORM_REDIS</c> — e.g. <c>localhost:6379</c>. Null means single-instance,
    /// in-memory job signaling (see <see cref="Context.Application.Jobs.IJobSignal"/>).</summary>
    public string? RedisConnectionString { get; init; }

    /// <summary>
    /// Host header values this process will answer to, enforced by <c>UseHostFiltering</c>.
    /// Binding to loopback keeps remote sockets out but does nothing about DNS rebinding: an
    /// attacker's page re-resolves its own domain to 127.0.0.1 and the victim's browser then talks
    /// to this host from script, with a Host header of <c>evil.example</c>. Rejecting unknown Host
    /// values closes that, so the loopback default is the exact set of names that legitimately
    /// resolve to loopback. A non-loopback bind (containers) can't know its own external name, so
    /// it defaults to <c>*</c> — set <c>CONTEXT_PLATFORM_ALLOWED_HOSTS</c> (';'-separated) there.
    /// </summary>
    public required IReadOnlyList<string> AllowedHosts { get; init; }

    public static ContextHostOptions FromEnvironment(IConfiguration configuration)
    {
        var root = Path.GetFullPath(
            configuration["CONTEXT_PLATFORM_ROOT"] is { Length: > 0 } configuredRoot
                ? configuredRoot
                : FindDefaultProjectRoot());

        var dataDir = SharedDataLocation.ResolveDataDir(configuration);
        var dbPath = SharedDataLocation.ResolveDbPath(configuration);

        var port = DefaultPort;
        if (configuration["CONTEXT_PLATFORM_PORT"] is { Length: > 0 } configuredPort
            && int.TryParse(configuredPort, out var parsedPort))
        {
            port = parsedPort;
        }

        var inContainer = string.Equals(configuration["DOTNET_RUNNING_IN_CONTAINER"], "true", StringComparison.OrdinalIgnoreCase);
        var bindHost = configuration["CONTEXT_PLATFORM_BIND_HOST"] is { Length: > 0 } configuredBindHost
            ? configuredBindHost
            : inContainer ? "0.0.0.0" : "127.0.0.1";

        var redisConnectionString = configuration["CONTEXT_PLATFORM_REDIS"] is { Length: > 0 } configuredRedis
            ? configuredRedis
            : null;

        var allowedHosts = configuration["CONTEXT_PLATFORM_ALLOWED_HOSTS"] is { Length: > 0 } configuredAllowedHosts
            ? ParseAllowedHosts(configuredAllowedHosts)
            : DefaultAllowedHosts(bindHost);

        return new ContextHostOptions
        {
            ProjectRoot = root,
            ProjectDataDir = dataDir,
            DbPath = dbPath,
            Port = port,
            BindHost = bindHost,
            RedisConnectionString = redisConnectionString,
            AllowedHosts = allowedHosts
        };
    }

    private static IReadOnlyList<string> ParseAllowedHosts(string configured) =>
        [.. configured.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static IReadOnlyList<string> DefaultAllowedHosts(string bindHost) =>
        System.Net.IPAddress.TryParse(bindHost, out var address) && System.Net.IPAddress.IsLoopback(address)
            ? ["localhost", "127.0.0.1", "[::1]"]
            : ["*"];

    // `dotnet run` (unlike Visual Studio's F5) does NOT apply launchSettings.json's
    // "workingDirectory", so Directory.GetCurrentDirectory() depends on whatever folder the shell
    // happened to be in - e.g. running from apps/host/Context.Dashboard.Server itself silently binds to THAT
    // folder instead of the repo checked out around it, creating a second, disconnected
    // .context database with none of the settings/history the real one has. Prefer the nearest
    // ancestor of this binary's own (fixed) location that looks like a repo root instead; only
    // fall back to cwd if none is found (e.g. published standalone with no .git, where
    // CONTEXT_PLATFORM_ROOT is expected to be set explicitly anyway - see Dockerfile/deploy/).
    private static string FindDefaultProjectRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
        }

        return Directory.GetCurrentDirectory();
    }
}

