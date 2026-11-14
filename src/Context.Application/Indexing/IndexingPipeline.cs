using Context.Application.Abstractions;
using Context.Application.Jobs;
using Context.Domain.Enums;
using Context.Indexing.Abstractions;
using Context.Infrastructure.Abstractions;
using Context.Providers.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Application.Indexing;

/// <summary>
/// Default <see cref="IIndexingPipeline"/>. Orchestrates full-project scans, change detection,
/// deletion cleanup, and dispatches per-file indexing to <see cref="ParsedFileIndexer"/> or
/// <see cref="UnparsedFileIndexer"/>.
/// </summary>
public sealed class IndexingPipeline : IIndexingPipeline
{
    private static readonly int MaxReindexParallelism = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);

    private readonly IProjectRepository _projects;
    private readonly IFileRepository _files;
    private readonly ISymbolRepository _symbols;
    private readonly IChunkRepository _chunks;
    private readonly IFileScanner _scanner;
    private readonly Func<string, IIgnoreMatcher> _ignoreFactory;
    private readonly IEnumerable<ILanguageParser> _parsers;
    private readonly IContentHasher _hasher;
    private readonly IJobQueue _jobQueue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IndexingPipeline> _logger;
    private readonly IIndexingProgressReporter _progressReporter;
    private readonly UnparsedFileIndexer _unparsedIndexer;
    private readonly ParsedFileIndexer _parsedIndexer;

    public IndexingPipeline(
        IProjectRepository projects,
        IFileRepository files,
        ISymbolRepository symbols,
        IChunkRepository chunks,
        IEmbeddingRepository embeddings,
        IProviderResolver providerResolver,
        IFileScanner scanner,
        Func<string, IIgnoreMatcher> ignoreFactory,
        IEnumerable<ILanguageParser> parsers,
        IContentHasher hasher,
        IJobQueue jobQueue,
        IServiceScopeFactory scopeFactory,
        ILogger<IndexingPipeline>? logger = null,
        IIndexingProgressReporter? progressReporter = null,
        IFileSummarizer? fileSummarizer = null)
    {
        _projects = projects;
        _files = files;
        _symbols = symbols;
        _chunks = chunks;
        _scanner = scanner;
        _ignoreFactory = ignoreFactory;
        _parsers = parsers;
        _hasher = hasher;
        _jobQueue = jobQueue;
        _scopeFactory = scopeFactory;
        _logger = logger ?? NullLogger<IndexingPipeline>.Instance;
        _progressReporter = progressReporter ?? NullIndexingProgressReporter.Instance;

        var embedScheduler = new ChunkEmbedScheduler(embeddings, providerResolver, jobQueue);
        _unparsedIndexer = new UnparsedFileIndexer(symbols, chunks, hasher, jobQueue, embedScheduler);
        _parsedIndexer = new ParsedFileIndexer(
            files, symbols, chunks, hasher, jobQueue, embedScheduler,
            fileSummarizer ?? NullFileSummarizer.Instance, _logger);
    }

    public Task IndexFileAsync(Guid projectId, string relativePath, CancellationToken ct) =>
        IndexFileInternalAsync(projectId, relativePath, forceReprocess: false, ct);

    public Task IndexFileAsync(Guid projectId, string relativePath, bool forceReprocess, CancellationToken ct) =>
        IndexFileInternalAsync(projectId, relativePath, forceReprocess, ct);

    public async Task ReindexProjectAsync(Guid projectId, bool full, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project {projectId} was not found.");

        var ignore = _ignoreFactory(project.RootPath);
        var scannedFiles = new List<ScannedFile>();
        await foreach (var scanned in _scanner.ScanAsync(project.RootPath, ignore, ct).WithCancellation(ct))
        {
            scannedFiles.Add(scanned);
        }

        var scannedPaths = scannedFiles
            .Select(f => NormalizePath(f.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var filesTotal = scannedFiles.Count;
        var filesProcessed = 0;
        var filesFailed = 0;

        await Parallel.ForEachAsync(
            scannedFiles,
            new ParallelOptions { MaxDegreeOfParallelism = MaxReindexParallelism, CancellationToken = ct },
            async (scanned, token) =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var scopedPipeline = scope.ServiceProvider.GetRequiredService<IIndexingPipeline>();
                    await scopedPipeline.IndexFileAsync(projectId, scanned.RelativePath, forceReprocess: full, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref filesFailed);
                    _logger.LogError(
                        ex, "Failed to index {RelativePath} in project {ProjectId} during reindex; queueing a retry.",
                        scanned.RelativePath, projectId);
                    await QueueFileRetryAsync(projectId, scanned.RelativePath).ConfigureAwait(false);
                }

                Interlocked.Increment(ref filesProcessed);
                await ReportProgressAsync(new ReindexProgressDto(projectId, filesProcessed, filesTotal, Completed: false, filesFailed), token)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);

        await CleanupRemovedFilesAsync(projectId, scannedPaths, ct).ConfigureAwait(false);

        await _jobQueue.EnqueueAsync(JobType.ResolveRelations, new ResolveRelationsPayload(projectId), projectId, ct: ct)
            .ConfigureAwait(false);

        await ReportProgressAsync(new ReindexProgressDto(projectId, filesTotal, filesTotal, Completed: true, filesFailed), ct)
            .ConfigureAwait(false);
    }

    private async Task CleanupRemovedFilesAsync(Guid projectId, HashSet<string> scannedPaths, CancellationToken ct)
    {
        var knownFiles = await _files.ListByProjectAsync(projectId, includeDeleted: false, ct).ConfigureAwait(false);
        foreach (var known in knownFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (!scannedPaths.Contains(NormalizePath(known.RelativePath)))
            {
                await HandleFileRemovedAsync(projectId, known.RelativePath, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task QueueFileRetryAsync(Guid projectId, string relativePath)
    {
        try
        {
            await _jobQueue.EnqueueAsync(JobType.IndexFile, new IndexFilePayload(projectId, relativePath), projectId, ct: CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not queue a retry of {RelativePath} in project {ProjectId}.", relativePath, projectId);
        }
    }

    private async Task ReportProgressAsync(ReindexProgressDto progress, CancellationToken ct)
    {
        try
        {
            await _progressReporter.ReportAsync(progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to report reindex progress for project {ProjectId}.", progress.ProjectId);
        }
    }

    private async Task IndexFileInternalAsync(Guid projectId, string relativePath, bool forceReprocess, CancellationToken ct)
    {
        var project = await _projects.GetByIdAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project {projectId} was not found.");

        var absolutePath = Path.Combine(project.RootPath, relativePath);
        if (!System.IO.File.Exists(absolutePath))
        {
            await HandleFileRemovedAsync(projectId, relativePath, ct).ConfigureAwait(false);
            return;
        }

        var contentHash = _hasher.HashFile(absolutePath);
        var existingFile = await _files.GetByRelativePathAsync(projectId, relativePath, ct).ConfigureAwait(false);

        if (!forceReprocess && existingFile is { IsDeleted: false } && existingFile.ContentHash == contentHash)
        {
            return;
        }

        var sourceText = await System.IO.File.ReadAllTextAsync(absolutePath, ct).ConfigureAwait(false);
        var extension = Path.GetExtension(relativePath);
        var parser = _parsers.FirstOrDefault(p => p.SupportedExtensions.Contains(extension));

        var fileEntity = new DomainFile
        {
            Id = existingFile?.Id ?? Guid.NewGuid(),
            ProjectId = projectId,
            RelativePath = relativePath,
            Language = LanguageFromExtension(extension),
            ContentHash = string.Empty,
            SizeBytes = new FileInfo(absolutePath).Length,
            IsDeleted = false,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        fileEntity = await _files.UpsertAsync(fileEntity, ct).ConfigureAwait(false);

        var isFirstIndex = existingFile is null || existingFile.ContentHash.Length == 0;
        if (parser is null)
        {
            await _unparsedIndexer.IndexAsync(projectId, fileEntity, sourceText, isFirstIndex, ct).ConfigureAwait(false);
        }
        else
        {
            await _parsedIndexer.IndexAsync(projectId, fileEntity, parser, sourceText, isFirstIndex, ct).ConfigureAwait(false);
        }

        await _files.SetContentHashAsync(fileEntity.Id, contentHash, ct).ConfigureAwait(false);
    }

    private async Task HandleFileRemovedAsync(Guid projectId, string relativePath, CancellationToken ct)
    {
        var existingFile = await _files.GetByRelativePathAsync(projectId, relativePath, ct).ConfigureAwait(false);
        if (existingFile is null || existingFile.IsDeleted)
        {
            return;
        }

        await _chunks.ReplaceForFileAsync(existingFile.Id, [], ct).ConfigureAwait(false);
        await _symbols.ReplaceForFileAsync(existingFile.Id, [], ct).ConfigureAwait(false);
        await _jobQueue.EnqueueAsync(JobType.UpdateGraph, new UpdateGraphPayload(projectId, existingFile.Id, Invalidate: true), projectId, ct: ct)
            .ConfigureAwait(false);
        await _files.MarkDeletedAsync(existingFile.Id, ct).ConfigureAwait(false);
    }

    private static string NormalizePath(string relativePath) => relativePath.Replace('\\', '/');

    private static string? LanguageFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".ts" or ".tsx" => "typescript",
        ".js" or ".jsx" => "javascript",
        ".cs" => "csharp",
        ".py" => "python",
        _ => null
    };
}
