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
/// path (400ms) so a burst of writes to the same file — an editor's atomic save, or several tool
/// calls in a row — triggers one reindex, not several.
/// <para/>
/// Extracted from the old single-project <c>FileWatcherHostedService</c> (see git history) once a
/// shared host could no longer assume exactly one bound project. One instance per project;
/// <see cref="ProjectFileWatcherManager"/> owns the project-to-instance mapping, constructs these,
/// and disposes them.
/// </summary>
/// <remarks>
/// <b>Known gap (accepted per the original Context.Dashboard.Server task brief §3, carried over unchanged):</b>
/// this is a minimal watcher, not a rename-aware one. <see cref="IIndexingPipeline"/> exposes only
/// a per-path <see cref="IIndexingPipeline.IndexFileAsync"/> — no "move" operation — so a rename is
/// scheduled as two independent reindexes: the old path (treated as a delete once nothing exists
/// there any more) and the new path as a fresh index. A renamed file's symbols therefore get new
/// <see cref="Guid"/> ids rather than being carried over; cross-references (graph nodes, memory
/// links) keyed on the old symbol ids are not preserved across the rename.
/// </remarks>
public sealed class ProjectFileWatcher : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan DefaultEnqueueRetryDelay = TimeSpan.FromSeconds(5);
    private const int MaxEnqueueAttempts = 12;

    private readonly Guid _projectId;
    private readonly string _root;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly IIgnoreMatcher _ignore;
    private readonly FileSystemWatcher? _watcher;
    private readonly CancellationTokenSource? _pollCts;

    private readonly ConcurrentDictionary<string, Timer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _enqueueFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _enqueueRetryDelay;
    private volatile bool _disposed;

    /// <summary>
    /// Starts watching immediately; a non-null <paramref name="pollingInterval"/> polls instead of
    /// using file-system events, taking its baseline snapshot before returning. Callers
    /// (<see cref="ProjectFileWatcherManager"/>) are expected to have already verified
    /// <paramref name="project"/>.<see cref="Project.RootPath"/> exists on disk — both modes throw
    /// otherwise, and this type does not repeat that check so the caller's warning/skip decision has
    /// exactly one place to live.
    /// </summary>
    public ProjectFileWatcher(
        Project project,
        IServiceScopeFactory scopeFactory,
        Func<string, IIgnoreMatcher> ignoreMatcherFactory,
        ILogger logger,
        TimeSpan? pollingInterval = null,
        TimeSpan? enqueueRetryDelay = null)
    {
        _enqueueRetryDelay = enqueueRetryDelay ?? DefaultEnqueueRetryDelay;
        _projectId = project.Id;
        _root = project.RootPath;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _ignore = ignoreMatcherFactory(_root);

        if (pollingInterval is { } interval)
        {
            var stopwatch = Stopwatch.StartNew();
            var baseline = TakeSnapshot();
            _logger.LogInformation(
                "Polling '{Root}' for project {ProjectId} file changes every {IntervalSeconds}s ({FileCount} files, first scan took {ElapsedMs}ms).",
                _root, _projectId, interval.TotalSeconds, baseline.Count, stopwatch.ElapsedMilliseconds);

            _pollCts = new CancellationTokenSource();
            _ = Task.Run(() => PollAsync(baseline, interval, _pollCts.Token));
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

    /// <summary>An error (typically an internal buffer overflow) means events were lost, and nothing says which
    /// files changed: an incremental reindex re-checks every file's hash, which is the only way to catch up.</summary>
    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger.LogWarning(e.GetException(), "FileSystemWatcher raised an error for project {ProjectId} root '{Root}'; queueing an incremental reindex to catch up.", _projectId, _root);
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ReindexRequester>()
                    .RequestAsync(_projectId, full: false, skipIfActive: true, CancellationToken.None).ConfigureAwait(false);
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

        // Hard, non-overridable skip for Context Platform's own data directory: without this, every
        // write the indexing pipeline itself makes (context.db and its -wal/-shm siblings, log
        // files) would re-trigger this very watcher, which would trigger another index pass,
        // forever. GitignoreStyleMatcher's built-in denylist doesn't know about ".context/" — that's
        // a Context.Dashboard.Server concern, not a generic ignore rule — so this check runs independently of,
        // and in addition to, `_ignore` below.
        if (IsContextDataPath(normalized))
        {
            return;
        }

        if (Directory.Exists(fullPath))
        {
            return; // Directory-level event; the files inside raise their own events.
        }

        if (_ignore.IsIgnored(normalized))
        {
            return;
        }

        var timer = _debounceTimers.GetOrAdd(
            normalized, _ => new Timer(OnDebounceElapsed, normalized, Timeout.Infinite, Timeout.Infinite));
        timer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
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
                    return; // Disposed mid-scan; don't start debounce timers Dispose already swept.
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
                // Keep the old snapshot: a root that's briefly unreachable must not read as every file deleted.
                _logger.LogWarning(ex, "Polling scan of '{Root}' for project {ProjectId} failed; retrying next interval.", _root, _projectId);
            }
        }
    }

    // Prunes ignored directories before descending, like FileSystemScanner, so node_modules/bin/obj
    // cost nothing per scan. Symlinked directories aren't followed, to rule out cycles.
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

        var snapshot = new Dictionary<string, (DateTimeOffset, long)>(StringComparer.Ordinal);
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
        if (_debounceTimers.TryRemove(relativePath, out var timer))
        {
            timer.Dispose();
        }

        // Enqueued rather than indexed inline: a `git checkout` of a large branch fires thousands of
        // these within the debounce window, and running them as unbounded Task.Run continuations
        // would open a DbContext and an embedding-provider call per file at once. The queue caps
        // concurrency, retries on failure, and survives a restart mid-burst.
        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var queue = scope.ServiceProvider.GetRequiredService<IJobQueue>();
            try
            {
                await queue.EnqueueAsync(
                    JobType.IndexFile,
                    new IndexFilePayload(_projectId, relativePath),
                    _projectId,
                    ct: CancellationToken.None).ConfigureAwait(false);
                _enqueueFailures.TryRemove(relativePath, out _);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to enqueue a reindex of '{RelativePath}' for project {ProjectId} after a file-system change.", relativePath, _projectId);
                RetryEnqueueLater(relativePath);
            }
        });
    }

    /// <summary>Re-arms the file's debounce timer on a longer delay instead of dropping the change: the debounce
    /// timers live in memory only, so a swallowed failure would otherwise lose the change until the next edit.</summary>
    private void RetryEnqueueLater(string relativePath)
    {
        if (_disposed || _enqueueFailures.AddOrUpdate(relativePath, 1, (_, failures) => failures + 1) > MaxEnqueueAttempts)
        {
            _enqueueFailures.TryRemove(relativePath, out _);
            return;
        }

        var timer = _debounceTimers.GetOrAdd(
            relativePath, _ => new Timer(OnDebounceElapsed, relativePath, Timeout.Infinite, Timeout.Infinite));
        timer.Change(_enqueueRetryDelay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _disposed = true;
        _pollCts?.Cancel();
        _pollCts?.Dispose();

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }

        foreach (var timer in _debounceTimers.Values)
        {
            timer.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
