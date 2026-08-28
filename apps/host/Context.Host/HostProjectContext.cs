namespace Context.Host;

/// <summary>
/// The single <c>Project</c> this Context.Host process is bound to — the one rooted at
/// <see cref="ContextHostOptions.ProjectRoot"/>. Registered as an empty singleton and populated
/// exactly once, synchronously, in <c>Program.cs</c> right after the DI container is built and
/// before the host starts accepting requests or running any <c>IHostedService</c> (the file
/// watcher included) — every hook endpoint, the file watcher, and every MCP tool's default
/// <c>projectId</c> all read from this instance.
/// </summary>
public sealed class HostProjectContext
{
    public Guid ProjectId { get; private set; }

    public string ProjectRoot { get; private set; } = string.Empty;

    /// <summary>
    /// The bearer token from <c>.claude/context-platform.local.json</c>, or <c>null</c> if that
    /// file doesn't exist (no token was ever installed) or couldn't be parsed — in which case hook
    /// endpoints reject every request (see <c>Hooks.HookEndpoints</c>).
    /// </summary>
    public string? BearerToken { get; private set; }

    public void Initialize(Guid projectId, string projectRoot, string? bearerToken)
    {
        ProjectId = projectId;
        ProjectRoot = projectRoot;
        BearerToken = bearerToken;
    }
}
