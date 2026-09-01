namespace Context.Application;

/// <summary>
/// Composition-time options for <c>AddContextApplication</c>. <see cref="ProjectDataDir"/> is the
/// same <c>{repo-root}/.context/</c> directory whose <c>context.db</c> is passed to
/// <c>AddContextInfrastructure</c> (CONTRACTS.md §9) — kept here too because
/// <c>Context.Application.Jobs.Handlers.BackupDbJobHandler</c> needs it to place
/// <c>VACUUM INTO</c> backups under <c>{ProjectDataDir}/backups/</c>. <see cref="ProjectRoot"/> is
/// <c>{repo-root}</c> itself, needed by provider-options setup classes
/// (<c>Context.Application.Providers</c>) that read the Dashboard Settings tab's saved
/// <c>{ProjectRoot}/.claude/context-platform.local.json</c> override.
/// </summary>
public sealed class ContextApplicationOptions
{
    public required string ProjectRoot { get; init; }

    public required string ProjectDataDir { get; init; }
}
