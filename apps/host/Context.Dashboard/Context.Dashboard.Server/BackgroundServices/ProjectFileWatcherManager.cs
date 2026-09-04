using System.Collections.Concurrent;
using Context.Domain.Entities;
using Context.Indexing.Abstractions;
using Context.Infrastructure.Abstractions;

namespace Context.Dashboard.Server.BackgroundServices;

/// <summary>
/// Owns one <see cref="ProjectFileWatcher"/> per <see cref="Project"/> this host serves. Replaces
/// the old single-project <c>FileWatcherHostedService</c>, which assumed exactly one project bound
/// to the whole process — no longer true once <c>CurrentProjectAccessor</c> resolves "which
/// project" per-request (Phase 3) and one host can serve many projects at once.
/// <para/>
/// Registered as both a singleton and an <see cref="IHostedService"/> — the same
/// "singleton-plays-both-roles" idiom <c>Context.Application.Jobs.JobDispatcher</c> already uses
/// (see <c>Context.Application.ServiceCollectionExtensions.AddJobDispatcher</c>) — so
/// <see cref="StartAsync"/> gives every already-registered project a watcher at host startup, while
/// the same instance is resolvable by <c>Context.Dashboard.Server.Api.ProjectEndpoints</c> to call
/// <see cref="StartWatching"/> the moment a *new* project is created, with no host restart needed.
/// </summary>
public sealed class ProjectFileWatcherManager(
    IServiceScopeFactory scopeFactory,
    Func<string, IIgnoreMatcher> ignoreMatcherFactory,
    ILoggerFactory loggerFactory) : IHostedService
{
    private readonly ILogger _managerLogger = loggerFactory.CreateLogger<ProjectFileWatcherManager>();
    private readonly ILogger _watcherLogger = loggerFactory.CreateLogger<ProjectFileWatcher>();
    private readonly ConcurrentDictionary<Guid, ProjectFileWatcher> _watchers = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Long-running: intentionally not awaited so the host's StartAsync returns promptly - same
        // shape JobDispatcher.StartAsync already uses for StartProcessingAsync. IHostedService.
        // StartAsync implementations are awaited SEQUENTIALLY by the generic host, and Kestrel's own
        // hosted service (GenericWebHostService) is one of them - so blocking here delays Kestrel's
        // bind by exactly as long as this takes. new FileSystemWatcher(...).EnableRaisingEvents =
        // true (in ProjectFileWatcher's constructor, below) recursively registers inotify watches
        // and can take upwards of a minute - sometimes far longer - for a root path served over a
        // non-native filesystem such as WSL2's DrvFS mount for a Windows drive (/mnt/c/...).
        // Confirmed live: with exactly one such project registered, this alone starved Kestrel past
        // systemd's TimeoutStartSec on every single restart (Kestrel's BindAsync was cancelled
        // before ever getting a turn), even though nothing was actually deadlocked - StartWatching
        // for a project whose root doesn't exist is already fast/non-blocking (Directory.Exists
        // check), so this was only ever a problem for a root path that DOES exist but is slow to
        // watch.
        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var projectRepository = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
            var projects = await projectRepository.ListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var project in projects)
            {
                StartWatching(project);
            }
        }, cancellationToken);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var projectId in _watchers.Keys.ToArray())
        {
            StopWatching(projectId);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts watching <paramref name="project"/>'s root, replacing any watcher already running for
    /// it (e.g. after <see cref="Project.RootPath"/> changed). A root that doesn't exist on disk is
    /// skipped with a warning rather than crashing the caller — a shared host may have projects
    /// registered whose filesystem isn't currently accessible.
    /// </summary>
    public void StartWatching(Project project)
    {
        if (!Directory.Exists(project.RootPath))
        {
            _managerLogger.LogWarning(
                "Project {ProjectId} root '{Root}' does not exist; file watching is disabled for it.",
                project.Id, project.RootPath);
            return;
        }

        StopWatching(project.Id);
        _watchers[project.Id] = new ProjectFileWatcher(project, scopeFactory, ignoreMatcherFactory, _watcherLogger);
    }

    public void StopWatching(Guid projectId)
    {
        if (_watchers.TryRemove(projectId, out var watcher))
        {
            watcher.Dispose();
        }
    }
}
