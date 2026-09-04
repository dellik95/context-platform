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

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var projectRepository = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
        var projects = await projectRepository.ListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var project in projects)
        {
            StartWatching(project);
        }
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
