namespace Context.Host;

/// <summary>
/// Resolved startup configuration for this Context.Host process, computed once in
/// <c>Program.cs</c> from environment variables (with safe defaults so the host runs with zero
/// configuration):
/// <list type="bullet">
/// <item><c>CONTEXT_PLATFORM_ROOT</c> — the single project root this process serves (defaults to
/// the current working directory).</item>
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
/// </list>
/// </summary>
public sealed class ContextHostOptions
{
    /// <summary>Default port, matching <c>ClaudeIntegrationOptions.HostPort</c>'s own default.</summary>
    public const int DefaultPort = 47923;

    public required string ProjectRoot { get; init; }

    /// <summary><c>{ProjectRoot}/.context</c> — database, logs, and backups all live under here.</summary>
    public required string ProjectDataDir { get; init; }

    /// <summary><c>{ProjectDataDir}/context.db</c>.</summary>
    public required string DbPath { get; init; }

    public required int Port { get; init; }

    public required string BindHost { get; init; }

    public static ContextHostOptions FromEnvironment(IConfiguration configuration)
    {
        var root = Path.GetFullPath(
            configuration["CONTEXT_PLATFORM_ROOT"] is { Length: > 0 } configuredRoot
                ? configuredRoot
                : Directory.GetCurrentDirectory());

        var dataDir = Path.Combine(root, ".context");
        var dbPath = Path.Combine(dataDir, "context.db");

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

        return new ContextHostOptions
        {
            ProjectRoot = root,
            ProjectDataDir = dataDir,
            DbPath = dbPath,
            Port = port,
            BindHost = bindHost
        };
    }
}
