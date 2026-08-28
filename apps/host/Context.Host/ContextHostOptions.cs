namespace Context.Host;

/// <summary>
/// Resolved startup configuration for this Context.Host process, computed once in
/// <c>Program.cs</c> from environment variables (with safe defaults so the host runs with zero
/// configuration):
/// <list type="bullet">
/// <item><c>CONTEXT_PLATFORM_ROOT</c> — the single project root this process serves (defaults to
/// the current working directory).</item>
/// <item><c>CONTEXT_PLATFORM_PORT</c> — the port Kestrel binds to on <c>127.0.0.1</c> only
/// (defaults to <see cref="Context.Claude.Installation.ClaudeIntegrationOptions.HostPort"/>'s own
/// default, 47923, so a project installed via <c>context claude install</c> talks to this host with
/// zero extra configuration).</item>
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

        return new ContextHostOptions
        {
            ProjectRoot = root,
            ProjectDataDir = dataDir,
            DbPath = dbPath,
            Port = port
        };
    }
}
