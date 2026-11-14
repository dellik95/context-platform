using System.Collections.Concurrent;
using Context.Domain.Entities;
using Context.Indexing.Abstractions;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.Options;

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
    ILoggerFactory loggerFactory,
    IOptions<FileWatchingOptions>? options = null) : IHostedService, IDisposable
{
    private readonly FileWatchingOptions _options = options?.Value ?? new FileWatchingOptions();
    private readonly ILogger _managerLogger = loggerFactory.CreateLogger<ProjectFileWatcherManager>();
    private readonly ILogger _watcherLogger = loggerFactory.CreateLogger<ProjectFileWatcher>();
    private readonly ConcurrentDictionary<Guid, ProjectFileWatcher> _watchers = new();
    private readonly ConcurrentDictionary<Task, byte> _backgroundSetupTasks = new();
    private CancellationTokenSource _stopCts = new();
    private int _disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var stopToken = _stopCts.Token;
        // Long-running: intentionally not awaited so the host's StartAsync returns promptly — IHostedService
        // StartAsync calls run sequentially before Kestrel binds. Uses the manager's own lifetime token
        // (not the StartAsync startup token) so setup survives startup completion and cancels cleanly on StopAsync.
        var task = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var projectRepository = scope.ServiceProvider.GetRequiredService<IProjectRepository>();
                var projects = await projectRepository.ListAsync(stopToken).ConfigureAwait(false);

                foreach (var project in projects)
                {
                    if (stopToken.IsCancellationRequested)
                    {
                        break;
                    }

                    // One project's unreadable root must not leave every project after it unwatched.
                    try
                    {
                        StartWatching(project);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _managerLogger.LogError(ex, "Could not start watching project {ProjectId} at '{Root}'; the remaining projects are still being set up.", project.Id, project.RootPath);
                    }
                }
            }
            catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
            {
                // Normal shutdown.
            }
        }, CancellationToken.None);

        TrackBackgroundTask(task);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopCts.CancelAsync().ConfigureAwait(false);

        var pending = _backgroundSetupTasks.Keys.ToArray();
        if (pending.Length > 0)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown deadline or normal cancellation.
            }
        }

        foreach (var projectId in _watchers.Keys.ToArray())
        {
            StopWatching(projectId);
        }
    }

    /// <summary>
    /// Starts watching <paramref name="project"/>'s root, replacing any watcher already running for
    /// it (e.g. after <see cref="Project.RootPath"/> changed). A root that doesn't exist on disk is
    /// skipped with a warning rather than crashing the caller — a shared host may have projects
    /// registered whose filesystem isn't currently accessible.
    /// </summary>
    public void StartWatching(Project project)
    {
        if (_stopCts.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (!Directory.Exists(project.RootPath))
        {
            _managerLogger.LogWarning(
                "Project {ProjectId} root '{Root}' does not exist; file watching is disabled for it.",
                project.Id, project.RootPath);
            return;
        }

        StopWatching(project.Id);
        TimeSpan? pollingInterval = ShouldPoll(project.RootPath)
            ? TimeSpan.FromSeconds(Math.Max(0.5, _options.PollingIntervalSeconds))
            : null;
        _watchers[project.Id] = new ProjectFileWatcher(
            project, scopeFactory, ignoreMatcherFactory, _watcherLogger, pollingInterval, stopToken: _stopCts.Token);
    }

    private bool ShouldPoll(string rootPath) => _options.Mode switch
    {
        FileWatchMode.Polling => true,
        FileWatchMode.Events => false,
        _ => Environment.GetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER") is "1" or "true" or "True"
             || MountFileSystem.LacksReliableChangeEvents(rootPath),
    };

    /// <summary>
    /// <see cref="StartWatching"/> off the caller's thread, for request paths: registering the
    /// watches for a large root on DrvFS can take many minutes (see <see cref="StartAsync"/>).
    /// </summary>
    public void StartWatchingInBackground(Project project)
    {
        var stopToken = _stopCts.Token;
        if (stopToken.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var task = Task.Run(() =>
        {
            try
            {
                StartWatching(project);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _managerLogger.LogError(ex, "Could not start watching project {ProjectId} at '{Root}'.", project.Id, project.RootPath);
            }
        }, CancellationToken.None);

        TrackBackgroundTask(task);
    }

    private void TrackBackgroundTask(Task task)
    {
        _backgroundSetupTasks[task] = 0;
        _ = task.ContinueWith(
            static (t, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _),
            _backgroundSetupTasks,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void StopWatching(Guid projectId)
    {
        if (_watchers.TryRemove(projectId, out var watcher))
        {
            watcher.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopCts.Cancel();
        foreach (var projectId in _watchers.Keys.ToArray())
        {
            StopWatching(projectId);
        }

        _stopCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
