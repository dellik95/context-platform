using System.Collections.Concurrent;
using Context.Domain.Entities;
using Context.Indexing.Abstractions;
using Context.Infrastructure.Abstractions;
using Microsoft.Extensions.Options;

namespace Context.Dashboard.Server.BackgroundServices;

/// <summary>
/// Owns one <see cref="ProjectFileWatcher"/> per <see cref="Project"/> this host serves.
/// Registered as both a singleton and an <see cref="IHostedService"/> so startup wires existing
/// projects while API requests can attach or remove watchers without restarting.
/// </summary>
public sealed class ProjectFileWatcherManager(
    IServiceScopeFactory scopeFactory,
    Func<string, IIgnoreMatcher> ignoreMatcherFactory,
    ILoggerFactory loggerFactory,
    IOptions<FileWatchingOptions>? options = null) : IHostedService, IDisposable, IAsyncDisposable
{
    private readonly FileWatchingOptions _options = options?.Value ?? new FileWatchingOptions();
    private readonly ILogger _managerLogger = loggerFactory.CreateLogger<ProjectFileWatcherManager>();
    private readonly ILogger _watcherLogger = loggerFactory.CreateLogger<ProjectFileWatcher>();
    private readonly Lock _watchersLock = new();
    private readonly Dictionary<Guid, ProjectFileWatcher> _watchers = new();
    private readonly ConcurrentDictionary<Task, byte> _backgroundSetupTasks = new();
    private readonly CancellationTokenSource _stopCts = new();
    private int _disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var stopToken = _stopCts.Token;
        // Intentionally backgrounded so IHostedService.StartAsync returns before Kestrel binds.
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

                    try
                    {
                        StartWatching(project);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _managerLogger.LogError(ex, "Could not start watching project {ProjectId} at '{Root}'; remaining projects continue.", project.Id, project.RootPath);
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

        ProjectFileWatcher[] active;
        lock (_watchersLock)
        {
            active = [.. _watchers.Values];
            _watchers.Clear();
        }

        foreach (var watcher in active)
        {
            await watcher.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void StartWatching(Project project)
    {
        if (IsShuttingDown())
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

        TimeSpan? pollingInterval = ShouldPoll(project.RootPath)
            ? TimeSpan.FromSeconds(Math.Max(0.5, _options.PollingIntervalSeconds))
            : null;

        var nextWatcher = new ProjectFileWatcher(
            project, scopeFactory, ignoreMatcherFactory, _watcherLogger, pollingInterval, stopToken: _stopCts.Token);

        ProjectFileWatcher? previous = null;
        var keepNew = false;
        lock (_watchersLock)
        {
            if (!IsShuttingDown())
            {
                _watchers.TryGetValue(project.Id, out previous);
                _watchers[project.Id] = nextWatcher;
                keepNew = true;
            }
        }

        if (!keepNew)
        {
            nextWatcher.Dispose();
            return;
        }

        previous?.Dispose();
    }

    public void StartWatchingInBackground(Project project)
    {
        if (IsShuttingDown())
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

    public void StopWatching(Guid projectId)
    {
        ProjectFileWatcher? watcher;
        lock (_watchersLock)
        {
            _watchers.Remove(projectId, out watcher);
        }

        watcher?.Dispose();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopCts.Cancel();
        ProjectFileWatcher[] active;
        lock (_watchersLock)
        {
            active = [.. _watchers.Values];
            _watchers.Clear();
        }

        foreach (var watcher in active)
        {
            watcher.Dispose();
        }

        _stopCts.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stopCts.CancelAsync().ConfigureAwait(false);
        var pending = _backgroundSetupTasks.Keys.ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        ProjectFileWatcher[] active;
        lock (_watchersLock)
        {
            active = [.. _watchers.Values];
            _watchers.Clear();
        }

        foreach (var watcher in active)
        {
            await watcher.DisposeAsync().ConfigureAwait(false);
        }

        _stopCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool IsShuttingDown() =>
        Volatile.Read(ref _disposed) != 0 || _stopCts.IsCancellationRequested;

    private bool ShouldPoll(string rootPath) => _options.Mode switch
    {
        FileWatchMode.Polling => true,
        FileWatchMode.Events => false,
        _ => Environment.GetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER") is "1" or "true" or "True"
             || MountFileSystem.LacksReliableChangeEvents(rootPath),
    };

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
}
