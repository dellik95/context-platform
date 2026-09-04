using System.Collections.Concurrent;
using Context.Application.Abstractions;
using Context.Indexing.Abstractions;

namespace Context.Host.BackgroundServices;

/// <summary>
/// Watches the current project's root (<see cref="HostProjectContext.ProjectRoot"/>) with a
/// <see cref="FileSystemWatcher"/> and calls <see cref="IIndexingPipeline.IndexFileAsync"/> for
/// every changed/created/deleted/renamed file, debounced per path (400ms) so a burst of writes to
/// the same file — an editor's atomic save, or several tool calls in a row — triggers one reindex,
/// not several.
/// </summary>
/// <remarks>
/// <b>Known gap (accepted per the Context.Host task brief §3):</b> this is a minimal watcher, not a
/// rename-aware one. <see cref="IIndexingPipeline"/> exposes only a per-path
/// <see cref="IIndexingPipeline.IndexFileAsync"/> — no "move" operation — so a rename is scheduled
/// as two independent reindexes: the old path (which <c>IndexFileAsync</c> treats as a delete once
/// nothing exists there any more) and the new path as a fresh index. A renamed file's symbols
/// therefore get new <see cref="Guid"/> ids rather than being carried over (the same outcome as a
/// delete immediately followed by an unrelated file being created at the new path) — cross-references
/// (graph nodes, memory links) keyed on the old symbol ids are not preserved across the rename.
/// A real rename-aware pipeline would need <see cref="IIndexingPipeline"/> itself to grow a
/// dedicated move/rename operation; out of scope for Context.Host.
/// </remarks>
public sealed class FileWatcherHostedService(
    IServiceScopeFactory scopeFactory,
    HostProjectContext hostProject,
    Func<string, IIgnoreMatcher> ignoreMatcherFactory,
    ILogger<FileWatcherHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(400);

    private readonly ConcurrentDictionary<string, Timer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private IIgnoreMatcher? _ignore;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // HostProjectContext.Initialize runs synchronously in Program.cs right after the DI
        // container is built and before any IHostedService (this one included) starts, so
        // ProjectRoot is already populated by the time ExecuteAsync runs - no wait/poll needed.
        var root = hostProject.ProjectRoot;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            logger.LogWarning(
                "FileWatcherHostedService: project root '{Root}' does not exist; file watching is disabled for this run.", root);
            return;
        }

        _ignore = ignoreMatcherFactory(root);
        _watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024
        };

        _watcher.Changed += OnFileSystemEvent;
        _watcher.Created += OnFileSystemEvent;
        _watcher.Deleted += OnFileSystemEvent;
        _watcher.Renamed += OnRenamed;
        _watcher.Error += (_, e) => logger.LogWarning(e.GetException(), "FileSystemWatcher raised an error for '{Root}'.", root);
        _watcher.EnableRaisingEvents = true;

        logger.LogInformation(
            "Watching '{Root}' for file changes (per-file debounce: {DebounceMs}ms).", root, DebounceDelay.TotalMilliseconds);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => ScheduleIndex(e.FullPath);

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        ScheduleIndex(e.OldFullPath);
        ScheduleIndex(e.FullPath);
    }

    private void ScheduleIndex(string fullPath)
    {
        var root = hostProject.ProjectRoot;
        if (string.IsNullOrEmpty(root))
        {
            return;
        }

        string relativePath;
        try
        {
            relativePath = Path.GetRelativePath(root, fullPath);
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
        // a Context.Host concern, not a generic ignore rule — so this check runs independently of,
        // and in addition to, `_ignore` below.
        if (normalized.Equals(".context", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".context/", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Directory.Exists(fullPath))
        {
            return; // Directory-level event; the files inside raise their own events.
        }

        if (_ignore?.IsIgnored(normalized) == true)
        {
            return;
        }

        var timer = _debounceTimers.GetOrAdd(
            normalized, _ => new Timer(OnDebounceElapsed, normalized, Timeout.Infinite, Timeout.Infinite));
        timer.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnDebounceElapsed(object? state)
    {
        var relativePath = (string)state!;
        if (_debounceTimers.TryRemove(relativePath, out var timer))
        {
            timer.Dispose();
        }

        _ = Task.Run(async () =>
        {
            using var scope = scopeFactory.CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<IIndexingPipeline>();
            try
            {
                await pipeline.IndexFileAsync(hostProject.ProjectId, relativePath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to reindex '{RelativePath}' after a file-system change.", relativePath);
            }
        });
    }

    /// <summary>
    /// Stops watching this host's bound project root without disposing the service itself --
    /// used when that project is deleted from Context Platform's own database (see
    /// <c>Context.Host.Api.ProjectEndpoints</c>'s <c>DELETE /api/projects/{id}</c>), which must not
    /// tear down the still-running <see cref="BackgroundService"/>. Idempotent: safe to call more
    /// than once, or when watching was never started (root didn't exist at startup).
    /// </summary>
    public void StopWatching()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
        }

        foreach (var timer in _debounceTimers.Values)
        {
            timer.Dispose();
        }

        _debounceTimers.Clear();
    }

    public override void Dispose()
    {
        StopWatching();
        _watcher?.Dispose();
        base.Dispose();
    }
}
