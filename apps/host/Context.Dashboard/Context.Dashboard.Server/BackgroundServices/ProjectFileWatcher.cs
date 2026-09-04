using System.Collections.Concurrent;
using Context.Application.Abstractions;
using Context.Domain.Entities;
using Context.Indexing.Abstractions;

namespace Context.Dashboard.Server.BackgroundServices;

/// <summary>
/// Watches one <see cref="Project"/>'s <see cref="Project.RootPath"/> with a
/// <see cref="FileSystemWatcher"/> and calls <see cref="IIndexingPipeline.IndexFileAsync"/> for
/// every changed/created/deleted/renamed file, debounced per path (400ms) so a burst of writes to
/// the same file — an editor's atomic save, or several tool calls in a row — triggers one reindex,
/// not several.
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

    private readonly Guid _projectId;
    private readonly string _root;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly IIgnoreMatcher _ignore;
    private readonly FileSystemWatcher _watcher;

    private readonly ConcurrentDictionary<string, Timer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Starts watching immediately. Callers (<see cref="ProjectFileWatcherManager"/>) are expected
    /// to have already verified <paramref name="project"/>.<see cref="Project.RootPath"/> exists on
    /// disk — <see cref="FileSystemWatcher"/>'s own constructor throws otherwise, and this type does
    /// not repeat that check so the caller's warning/skip decision has exactly one place to live.
    /// </summary>
    public ProjectFileWatcher(
        Project project,
        IServiceScopeFactory scopeFactory,
        Func<string, IIgnoreMatcher> ignoreMatcherFactory,
        ILogger logger)
    {
        _projectId = project.Id;
        _root = project.RootPath;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _ignore = ignoreMatcherFactory(_root);

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
        _watcher.Error += (_, e) =>
            _logger.LogWarning(e.GetException(), "FileSystemWatcher raised an error for project {ProjectId} root '{Root}'.", _projectId, _root);
        _watcher.EnableRaisingEvents = true;

        _logger.LogInformation(
            "Watching '{Root}' for project {ProjectId} file changes (per-file debounce: {DebounceMs}ms).",
            _root, _projectId, DebounceDelay.TotalMilliseconds);
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => ScheduleIndex(e.FullPath);

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
        if (normalized.Equals(".context", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".context/", StringComparison.OrdinalIgnoreCase))
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

    private void OnDebounceElapsed(object? state)
    {
        var relativePath = (string)state!;
        if (_debounceTimers.TryRemove(relativePath, out var timer))
        {
            timer.Dispose();
        }

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<IIndexingPipeline>();
            try
            {
                await pipeline.IndexFileAsync(_projectId, relativePath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reindex '{RelativePath}' for project {ProjectId} after a file-system change.", relativePath, _projectId);
            }
        });
    }

    public void Dispose()
    {
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();

        foreach (var timer in _debounceTimers.Values)
        {
            timer.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
