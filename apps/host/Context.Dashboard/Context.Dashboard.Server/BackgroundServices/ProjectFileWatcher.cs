using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;
using Context.Application.Abstractions;
using Context.Application.Indexing;
using Context.Application.Jobs;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Indexing.Abstractions;

namespace Context.Dashboard.Server.BackgroundServices;

/// <summary>
/// Watches one <see cref="Project"/>'s <see cref="Project.RootPath"/> — with a
/// <see cref="FileSystemWatcher"/>, or by polling a snapshot of the non-ignored tree where
/// file-system events are slow or unreliable (see <see cref="MountFileSystem"/>) — and enqueues a
/// <see cref="JobType.IndexFile"/> job for every changed/created/deleted/renamed file, debounced per
/// path (400ms).
/// </summary>
public sealed class ProjectFileWatcher : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DefaultEnqueueRetryDelay = TimeSpan.FromSeconds(5);
    private const int MaxEnqueueAttempts = 12;
    private const int MaxConcurrentEnqueues = 4;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    private readonly Guid _projectId;
    private readonly string _root;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly IIgnoreMatcher _ignore;
    private readonly FileSystemWatcher? _watcher;
    private readonly CancellationTokenSource _disposeCts;
    private readonly CancellationToken _stopToken;

    private readonly Lock _timerLock = new();
    private readonly Dictionary<string, Timer> _debounceTimers = new(PathComparer);
    private readonly ConcurrentDictionary<string, int> _enqueueFailures = new(PathComparer);
    private readonly ConcurrentDictionary<Task, byte> _inFlightTasks = new();
    private readonly SemaphoreSlim _enqueueGate = new(MaxConcurrentEnqueues, MaxConcurrentEnqueues);
    private readonly TimeSpan _enqueueRetryDelay;
    private volatile bool _disposed;

    public ProjectFileWatcher(
        Project project,
        IServiceScopeFactory scopeFactory,
        Func<string, IIgnoreMatcher> ignoreMatcherFactory,
        ILogger logger,
        TimeSpan? pollingInterval = null,
        TimeSpan? enqueueRetryDelay = null,
        CancellationToken stopToken = default)
    {
        _enqueueRetryDelay = enqueueRetryDelay ?? DefaultEnqueueRetryDelay;
        _projectId = project.Id;
        _root = project.RootPath;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _ignore = ignoreMatcherFactory(_root);
        _disposeCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _stopToken = _disposeCts.Token;

        if (pollingInterval is { } interval)
        {
            var stopwatch = Stopwatch.StartNew();
            var baseline = TakeSnapshot();
            _logger.LogInformation(
                "Polling '{Root}' for project {ProjectId} file changes every {IntervalSeconds}s ({FileCount} files, first scan took {ElapsedMs}ms).",
                _root, _projectId, interval.TotalSeconds, baseline.Count, stopwatch.ElapsedMilliseconds);

            TrackTask(Task.Run(() => PollAsync(baseline, interval, _stopToken), CancellationToken.None));
            return;
        }

        _watcher = new FileSystemWatcher(_root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024
        };

        _watcher.Changed += OnFileSystemEvent;
        _watcher.Created += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;

        _logger.LogInformation(
            "Watching '{Root}' for project {ProjectId} file changes (per-file debounce: {DebounceMs}ms).",
            _root, _projectId, DebounceDelay.TotalMilliseconds);
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => ScheduleIndex(e.FullPath);

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        if (_disposed || _stopToken.IsCancellationRequested)
        {
            return;
        }

        _logger.LogWarning(e.GetException(), "FileSystemWatcher raised an error for project {ProjectId} root '{Root}'; queueing an incremental reindex to catch up.", _projectId, _root);
        RunTrackedBackground(async ct =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ReindexRequester>()
                    .RequestAsync(_projectId, full: false, skipIfActive: true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Watcher disposed during shutdown.
            }
            catch (ObjectDisposedException) when (_disposed)
            {
                // Scope factory disposed during shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not queue a catch-up reindex for project {ProjectId} after a file watcher error.", _projectId);
            }
        });
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        ScheduleIndex(e.OldFullPath);
        ScheduleIndex(e.FullPath);
    }

    private void ScheduleIndex(string fullPath)
    {
        string relativePath;
        try
        {
            relativePath = Path.GetRelativePath(_root, fullPath);
        }
        catch (ArgumentException)
        {
            return;
        }

        var normalized = relativePath.Replace('\\', '/');

        // Hard skip for Context Platform's own data directory so DB/log writes never self-trigger reindexes.
        if (IsContextDataPath(normalized) || Directory.Exists(fullPath) || _ignore.IsIgnored(normalized))
        {
            return;
        }

        ArmDebounceTimer(normalized, DebounceDelay);
    }

    private void ArmDebounceTimer(string relativePath, TimeSpan delay)
    {
        lock (_timerLock)
        {
            if (_disposed || _stopToken.IsCancellationRequested)
            {
                return;
            }

            if (!_debounceTimers.TryGetValue(relativePath, out var timer))
            {
                timer = new Timer(OnDebounceElapsed, relativePath, Timeout.Infinite, Timeout.Infinite);
                _debounceTimers[relativePath] = timer;
            }

            timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private static bool IsContextDataPath(string normalized) =>
        normalized.Equals(".context", StringComparison.OrdinalIgnoreCase) ||
        normalized.StartsWith(".context/", StringComparison.OrdinalIgnoreCase);

    private async Task PollAsync(Dictionary<string, (DateTimeOffset Modified, long Length)> snapshot, TimeSpan interval, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                var current = TakeSnapshot();
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                foreach (var (path, stamp) in current)
                {
                    if (!snapshot.TryGetValue(path, out var previous) || previous != stamp)
                    {
                        ScheduleIndex(Path.Combine(_root, path));
                    }
                }

                foreach (var path in snapshot.Keys)
                {
                    if (!current.ContainsKey(path))
                    {
                        ScheduleIndex(Path.Combine(_root, path));
                    }
                }

                snapshot = current;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Polling scan of '{Root}' for project {ProjectId} failed; retrying next interval.", _root, _projectId);
            }
        }
    }

    private Dictionary<string, (DateTimeOffset Modified, long Length)> TakeSnapshot()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };

        var entries = new FileSystemEnumerable<(string Path, DateTimeOffset Modified, long Length)>(
            _root,
            (ref FileSystemEntry entry) => (RelativePath(ref entry), entry.LastWriteTimeUtc, entry.Length),
            options)
        {
            ShouldRecursePredicate = (ref FileSystemEntry entry) =>
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                var relative = RelativePath(ref entry);
                return !IsContextDataPath(relative) && !_ignore.IsIgnored(relative + "/") && !_ignore.IsIgnored(relative);
            },
            ShouldIncludePredicate = (ref FileSystemEntry entry) =>
            {
                if (entry.IsDirectory)
                {
                    return false;
                }

                var relative = RelativePath(ref entry);
                return !IsContextDataPath(relative) && !_ignore.IsIgnored(relative);
            },
        };

        var snapshot = new Dictionary<string, (DateTimeOffset, long)>(PathComparer);
        foreach (var (path, modified, length) in entries)
        {
            snapshot[path] = (modified, length);
        }

        return snapshot;
    }

    private string RelativePath(ref FileSystemEntry entry) =>
        Path.GetRelativePath(_root, entry.ToFullPath()).Replace('\\', '/');

    private void OnDebounceElapsed(object? state)
    {
        var relativePath = (string)state!;
        lock (_timerLock)
        {
            if (_debounceTimers.Remove(relativePath, out var timer))
            {
                timer.Dispose();
            }

            if (_disposed || _stopToken.IsCancellationRequested)
            {
                return;
            }
        }

        // Enqueue via a bounded semaphore so branch switches do not flood SQLite with thousands of parallel DbContexts.
        RunTrackedBackground(async ct =>
        {
            var acquired = false;
            try
            {
                await _enqueueGate.WaitAsync(ct).ConfigureAwait(false);
                acquired = true;
                using var scope = _scopeFactory.CreateScope();
                var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
                await queue.EnqueueAsync(
                    JobType.IndexFile,
                    new IndexFilePayload(_projectId, relativePath),
                    _projectId,
                    ct: ct).ConfigureAwait(false);
                _enqueueFailures.TryRemove(relativePath, out _);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Watcher disposed during shutdown.
            }
            catch (ObjectDisposedException) when (_disposed)
            {
                // Semaphore or scope factory disposed during shutdown.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enqueue a reindex of '{RelativePath}' for project {ProjectId} after a file-system change.", relativePath, _projectId);
                RetryEnqueueLater(relativePath);
            }
            finally
            {
                if (acquired && !_disposed)
                {
                    try
                    {
                        _enqueueGate.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Disposed concurrently.
                    }
                }
            }
        });
    }

    private void RunTrackedBackground(Func<CancellationToken, Task> work)
    {
        if (_disposed || _stopToken.IsCancellationRequested)
        {
            return;
        }

        var token = _stopToken;
        TrackTask(Task.Run(() => work(token), CancellationToken.None));
    }

    private void TrackTask(Task task)
    {
        _inFlightTasks[task] = 0;
        _ = task.ContinueWith(
            static (t, state) => ((ConcurrentDictionary<Task, byte>)state!).TryRemove(t, out _),
            _inFlightTasks,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void RetryEnqueueLater(string relativePath)
    {
        if (_disposed || _enqueueFailures.AddOrUpdate(relativePath, 1, (_, failures) => failures + 1) > MaxEnqueueAttempts)
        {
            _enqueueFailures.TryRemove(relativePath, out _);
            return;
        }

        ArmDebounceTimer(relativePath, _enqueueRetryDelay);
    }

    public void Dispose()
    {
        if (!BeginDispose())
        {
            return;
        }

        _disposeCts.Dispose();
        _enqueueGate.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (!BeginDispose())
        {
            return;
        }

        var pending = _inFlightTasks.Keys.ToArray();
        if (pending.Length > 0)
        {
            await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _disposeCts.Dispose();
        _enqueueGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool BeginDispose()
    {
        Timer[] timersToDispose;
        lock (_timerLock)
        {
            if (_disposed)
            {
                return false;
            }

            _disposed = true;
            timersToDispose = [.. _debounceTimers.Values];
            _debounceTimers.Clear();
        }

        _disposeCts.Cancel();

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }

        foreach (var timer in timersToDispose)
        {
            timer.Dispose();
        }

        return true;
    }
}
