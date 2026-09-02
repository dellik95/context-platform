using Context.Application.Abstractions;
using Context.Application.Jobs;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Indexing;
using Context.Indexing.Abstractions;
using Context.Infrastructure.Abstractions;
using Context.Providers;
using Context.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Application.Indexing;

/// <summary>
/// Default <see cref="IIndexingPipeline"/>. Wires Context.Indexing's scanner/ignore-matcher/
/// hasher/parsers and <see cref="SymbolDiffer"/> together with the Context.Infrastructure
/// repositories, doing symbol-level incremental work per the task brief §4:
/// <list type="bullet">
/// <item>a file whose content hash hasn't changed is skipped entirely;</item>
/// <item>symbols are matched across reindex by <c>QualifiedName</c> and diffed by a hash of their
/// body text (via <see cref="SymbolDiffer"/>), reusing each matched symbol's previous
/// <see cref="Symbol.Id"/> so cross-references (graph nodes, memory links) stay stable;</item>
/// <item><c>EmbedChunk</c> is only enqueued for a chunk whose content hash has no
/// <see cref="Domain.Entities.Embedding"/> row yet;</item>
/// <item><c>UpdateGraph</c> is only enqueued when something about the file's symbol set actually
/// changed (or on first index).</item>
/// </list>
/// </summary>
/// <remarks>
/// <see cref="Symbol"/> only persists <see cref="Symbol.BodyHash"/> (a hash of raw body text
/// alone — currently write-only, not read back by anything), not text <see cref="SymbolDiffer.Diff"/>
/// could re-hash for comparison. This pipeline instead recovers a usable proxy for a symbol's
/// previous diff-text from its corresponding <see cref="Chunk.Text"/> (which is
/// <see cref="SymbolDiffer.CombinedText"/> of the symbol's signature and body — see
/// <see cref="IndexParsedFileAsync"/> — so re-diffing on it via an empty reconstructed
/// SignatureText, per <see cref="SymbolDiffer.CombinedText"/>'s own null/empty handling, reproduces
/// the same value). The one edge case this can't recover from is a symbol indexed
/// before this pairing existed (or whose chunk was otherwise lost): its proxy body text is empty,
/// which <see cref="SymbolDiffer"/> then reports as "Changed" even if the symbol didn't actually
/// change — a harmless one-time false positive that self-heals on the very next reindex.
/// </remarks>
public sealed class IndexingPipeline(
    IProjectRepository projects,
    IFileRepository files,
    ISymbolRepository symbols,
    IChunkRepository chunks,
    IEmbeddingRepository embeddings,
    IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IFileScanner scanner,
    Func<string, IIgnoreMatcher> ignoreFactory,
    IEnumerable<ILanguageParser> parsers,
    IContentHasher hasher,
    IJobQueue jobQueue,
    ILogger<IndexingPipeline>? logger = null,
    IIndexingProgressReporter? progressReporter = null,
    IFileSummarizer? fileSummarizer = null) : IIndexingPipeline
{
    /// <summary>Safety cap on a single chunk's text so one pathological symbol/file can't blow up storage/search.</summary>
    private const int MaxChunkChars = 20_000;

    private readonly ILogger<IndexingPipeline> _logger = logger ?? NullLogger<IndexingPipeline>.Instance;
    private readonly IIndexingProgressReporter _progressReporter = progressReporter ?? NullIndexingProgressReporter.Instance;
    private readonly IFileSummarizer _fileSummarizer = fileSummarizer ?? NullFileSummarizer.Instance;

    public Task IndexFileAsync(Guid projectId, string relativePath, CancellationToken ct) =>
        IndexFileInternalAsync(projectId, relativePath, forceReprocess: false, ct);

    public async Task ReindexProjectAsync(Guid projectId, bool full, CancellationToken ct)
    {
        var project = await projects.GetByIdAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project {projectId} was not found.");

        var ignore = ignoreFactory(project.RootPath);
        var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // FilesTotal must be known before the first progress report goes out, so the scan is
        // buffered into a list up front rather than driving IndexFileInternalAsync straight off
        // the IAsyncEnumerable as it streams in.
        var scannedFiles = new List<ScannedFile>();
        await foreach (var scanned in scanner.ScanAsync(project.RootPath, ignore, ct).WithCancellation(ct))
        {
            scannedFiles.Add(scanned);
        }

        var filesTotal = scannedFiles.Count;
        var filesProcessed = 0;
        var filesFailed = 0;

        foreach (var scanned in scannedFiles)
        {
            scannedPaths.Add(NormalizePath(scanned.RelativePath));

            // A single file that throws during parsing/indexing (malformed syntax, an
            // unreadable file, ...) must not abort the whole pass -- it would otherwise take
            // down the deleted-file cleanup and the completion signal below for every OTHER
            // file too. Log it, count it, and move on; the file self-heals on the next
            // scheduled pass (its ContentHash never advanced, so it's retried from scratch).
            try
            {
                await IndexFileInternalAsync(projectId, scanned.RelativePath, forceReprocess: full, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                filesFailed++;
                _logger.LogError(
                    ex, "Failed to index {RelativePath} in project {ProjectId} during reindex; skipping it for this pass.",
                    scanned.RelativePath, projectId);
            }

            filesProcessed++;
            await ReportProgressAsync(new ReindexProgressDto(projectId, filesProcessed, filesTotal, Completed: false, filesFailed), ct)
                .ConfigureAwait(false);
        }

        // Anything still on record for this project but not found by this scan has been deleted
        // (or moved out from under an ignore rule) since the last reindex. This must still run
        // even if some files above failed to index -- those files simply weren't touched, they
        // were never removed from scannedPaths, so their deletion status is unaffected.
        var knownFiles = await files.ListByProjectAsync(projectId, includeDeleted: false, ct).ConfigureAwait(false);
        foreach (var known in knownFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (!scannedPaths.Contains(NormalizePath(known.RelativePath)))
            {
                await HandleFileRemovedAsync(projectId, known.RelativePath, ct).ConfigureAwait(false);
            }
        }

        await ReportProgressAsync(new ReindexProgressDto(projectId, filesTotal, filesTotal, Completed: true, filesFailed), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Progress reporting is an observability side-channel, not part of the reindex itself — a
    /// broken/unreachable reporter (e.g. a dropped SignalR connection) must never abort the
    /// reindex it's reporting on.
    /// </summary>
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
        var project = await projects.GetByIdAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project {projectId} was not found.");

        var absolutePath = Path.Combine(project.RootPath, relativePath);

        if (!System.IO.File.Exists(absolutePath))
        {
            await HandleFileRemovedAsync(projectId, relativePath, ct).ConfigureAwait(false);
            return;
        }

        var contentHash = hasher.HashFile(absolutePath);
        var existingFile = await files.GetByRelativePathAsync(projectId, relativePath, ct).ConfigureAwait(false);

        // Incremental-indexing skip: identical content, nothing to do (spec §13 — "incremental
        // indexing must avoid reprocessing unchanged files").
        if (!forceReprocess && existingFile is { IsDeleted: false } && existingFile.ContentHash == contentHash)
        {
            return;
        }

        var sourceText = await System.IO.File.ReadAllTextAsync(absolutePath, ct).ConfigureAwait(false);
        var extension = Path.GetExtension(relativePath);
        var parser = parsers.FirstOrDefault(p => p.SupportedExtensions.Contains(extension));

        var fileEntity = new DomainFile
        {
            Id = existingFile?.Id ?? Guid.NewGuid(),
            ProjectId = projectId,
            RelativePath = relativePath,
            Language = LanguageFromExtension(extension),
            ContentHash = contentHash,
            SizeBytes = new FileInfo(absolutePath).Length,
            IsDeleted = false,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        fileEntity = await files.UpsertAsync(fileEntity, ct).ConfigureAwait(false);

        var isFirstIndex = existingFile is null;

        if (parser is null)
        {
            await IndexUnparsedFileAsync(projectId, fileEntity, sourceText, isFirstIndex, ct).ConfigureAwait(false);
            return;
        }

        await IndexParsedFileAsync(projectId, fileEntity, parser, sourceText, isFirstIndex, ct).ConfigureAwait(false);
    }

    private async Task HandleFileRemovedAsync(Guid projectId, string relativePath, CancellationToken ct)
    {
        var existingFile = await files.GetByRelativePathAsync(projectId, relativePath, ct).ConfigureAwait(false);
        if (existingFile is null || existingFile.IsDeleted)
        {
            return;
        }

        // Chunks first (Chunk.SymbolId is FK-restricted to Symbol), then symbols, then the graph.
        await chunks.ReplaceForFileAsync(existingFile.Id, [], ct).ConfigureAwait(false);
        await symbols.ReplaceForFileAsync(existingFile.Id, [], ct).ConfigureAwait(false);
        await jobQueue.EnqueueAsync(JobType.UpdateGraph, new UpdateGraphPayload(projectId, existingFile.Id, Invalidate: true), ct: ct)
            .ConfigureAwait(false);
        await files.MarkDeletedAsync(existingFile.Id, ct).ConfigureAwait(false);
    }

    private async Task IndexUnparsedFileAsync(Guid projectId, DomainFile fileEntity, string sourceText, bool isFirstIndex, CancellationToken ct)
    {
        // No ILanguageParser for this extension (e.g. JSON/markdown/config): still make the file
        // lexically/semantically searchable as a single whole-file chunk, just with no symbols.
        var text = Cap(sourceText, MaxChunkChars);
        var chunk = new Chunk
        {
            Id = Guid.NewGuid(),
            FileId = fileEntity.Id,
            SymbolId = null,
            Text = text,
            TokenCount = TokenEstimator.Estimate(text),
            ContentHash = hasher.HashText(text)
        };

        await symbols.ReplaceForFileAsync(fileEntity.Id, [], ct).ConfigureAwait(false);
        await chunks.ReplaceForFileAsync(fileEntity.Id, [chunk], ct).ConfigureAwait(false);

        await EnqueueEmbedIfMissingAsync(projectId, [chunk], ct).ConfigureAwait(false);

        if (isFirstIndex)
        {
            await jobQueue.EnqueueAsync(JobType.UpdateGraph, new UpdateGraphPayload(projectId, fileEntity.Id, Invalidate: false), ct: ct)
                .ConfigureAwait(false);
        }
    }

    private async Task IndexParsedFileAsync(
        Guid projectId, DomainFile fileEntity, ILanguageParser parser, string sourceText, bool isFirstIndex, CancellationToken ct)
    {
        var previous = await LoadPreviousSymbolDraftsAsync(fileEntity.Id, ct).ConfigureAwait(false);

        var ast = parser.Parse(sourceText);
        // A parser can legitimately extract two declarations under one QualifiedName (most
        // commonly method overloads, which this pipeline's QualifiedName scheme doesn't
        // disambiguate by parameter list) — SymbolDiffer.Diff already documents and accepts
        // "last one wins" for that case; deduplicate here too, on the SAME raw list SymbolDiffer
        // is about to see, so every symbol id assigned below is unique and this pipeline's own
        // Symbol/Chunk rows never try to attach two EF-tracked entities under one key.
        var currentDrafts = DeduplicateByQualifiedName(parser.ExtractSymbols(ast), fileEntity.RelativePath, _logger);
        var diff = SymbolDiffer.Diff(previous.Drafts, currentDrafts);

        // Preserve symbol identity by QualifiedName across reindex: a matched Added/Changed/
        // Unchanged symbol keeps its previous Id, so anything keyed on Symbol.Id (graph nodes —
        // see UpdateGraphJobHandler, which sets GraphNode.Id = Symbol.Id — and MemorySymbol
        // links) stays stable across edits that don't rename the symbol.
        var idByQualifiedName = new Dictionary<string, Guid>(currentDrafts.Count, StringComparer.Ordinal);
        foreach (var draft in currentDrafts)
        {
            idByQualifiedName[draft.QualifiedName] = previous.IdByQualifiedName.TryGetValue(draft.QualifiedName, out var previousId)
                ? previousId
                : Guid.NewGuid();
        }

        var newSymbolRows = new List<Symbol>(currentDrafts.Count);
        var newChunkRows = new List<Chunk>(currentDrafts.Count);

        foreach (var draft in currentDrafts)
        {
            var symbolId = idByQualifiedName[draft.QualifiedName];
            var parentId = draft.ParentQualifiedName is not null
                && idByQualifiedName.TryGetValue(draft.ParentQualifiedName, out var pid)
                    ? pid
                    : (Guid?)null;

            newSymbolRows.Add(new Symbol
            {
                Id = symbolId,
                FileId = fileEntity.Id,
                ParentSymbolId = parentId,
                Kind = draft.Kind,
                Name = draft.Name,
                QualifiedName = draft.QualifiedName,
                StartLine = draft.StartLine,
                EndLine = draft.EndLine,
                SignatureHash = hasher.HashText(draft.SignatureText ?? string.Empty),
                BodyHash = hasher.HashText(draft.BodyText ?? string.Empty),
                DocComment = draft.DocComment
            });

            // A parser reports BodyText == "" for a bodyless declaration (an interface member,
            // an abstract method, a property with no accessors block, ...) — see e.g.
            // TreeSitterCSharpParser's own documented "headerless" fallback for expression-bodied
            // members. Chunking on BodyText alone then produces an empty chunk: no lexical/vector
            // match ever finds meaningful text for it, and the resulting SearchHit falls back to a
            // bare-GUID label. Always leading with SignatureText fixes that (bodyless members are
            // then represented by their signature, which for those cases IS the whole declaration)
            // and is a genuine quality improvement even when a body exists — a chunk that reads
            // "public int Add(int a, int b) { return a + b; }" embeds/matches better than "return
            // a + b;" alone.
            var chunkText = Cap(SymbolDiffer.CombinedText(draft.SignatureText, draft.BodyText), MaxChunkChars);
            newChunkRows.Add(new Chunk
            {
                Id = Guid.NewGuid(),
                FileId = fileEntity.Id,
                SymbolId = symbolId,
                Text = chunkText,
                TokenCount = TokenEstimator.Estimate(chunkText),
                ContentHash = hasher.HashText(chunkText)
            });
        }

        // A whole-file summary is regenerated whenever content actually changed (this method only
        // runs on a changed/forced file to begin with) and is chunked/embedded the exact same way
        // as every symbol chunk above, just keyed by FileId instead of SymbolId (see
        // IndexUnparsedFileAsync's whole-file chunk for the same SymbolId == null precedent).
        var summary = await _fileSummarizer
            .SummarizeAsync(fileEntity.RelativePath, [.. currentDrafts.Select(d => d.Name)], ct)
            .ConfigureAwait(false);

        Chunk? summaryChunk = null;
        if (!string.IsNullOrWhiteSpace(summary))
        {
            fileEntity.Summary = summary;
            fileEntity = await files.UpsertAsync(fileEntity, ct).ConfigureAwait(false);

            var summaryText = Cap(summary, MaxChunkChars);
            summaryChunk = new Chunk
            {
                Id = Guid.NewGuid(),
                FileId = fileEntity.Id,
                SymbolId = null,
                Text = summaryText,
                TokenCount = TokenEstimator.Estimate(summaryText),
                ContentHash = hasher.HashText(summaryText)
            };
            newChunkRows.Add(summaryChunk);
        }

        // FK ordering: Chunk.SymbolId and Chunk.FileId are both restrict-on-delete against
        // Symbol/File. Old chunks (pointing at old symbol rows) must be cleared before the old
        // symbols can be deleted; new chunks (pointing at new symbol rows) can only be inserted
        // once those new symbols exist. Hence: clear chunks -> replace symbols -> insert chunks.
        await chunks.ReplaceForFileAsync(fileEntity.Id, [], ct).ConfigureAwait(false);
        await symbols.ReplaceForFileAsync(fileEntity.Id, newSymbolRows, ct).ConfigureAwait(false);
        await chunks.ReplaceForFileAsync(fileEntity.Id, newChunkRows, ct).ConfigureAwait(false);

        var changedOrAddedSymbolIds = diff.Added.Concat(diff.Changed)
            .Select(d => idByQualifiedName[d.QualifiedName])
            .ToHashSet();
        var chunksNeedingEmbedCheck = newChunkRows
            .Where(c => (c.SymbolId.HasValue && changedOrAddedSymbolIds.Contains(c.SymbolId.Value)) || c == summaryChunk)
            .ToList();
        await EnqueueEmbedIfMissingAsync(projectId, chunksNeedingEmbedCheck, ct).ConfigureAwait(false);

        var hasStructuralChange = diff.Added.Count > 0 || diff.Changed.Count > 0 || diff.Removed.Count > 0;
        if (hasStructuralChange || isFirstIndex)
        {
            await jobQueue.EnqueueAsync(JobType.UpdateGraph, new UpdateGraphPayload(projectId, fileEntity.Id, Invalidate: false), ct: ct)
                .ConfigureAwait(false);
        }
    }

    private async Task<PreviousSymbolInfo> LoadPreviousSymbolDraftsAsync(Guid fileId, CancellationToken ct)
    {
        var previousSymbolRows = await symbols.ListByFileAsync(fileId, ct).ConfigureAwait(false);
        if (previousSymbolRows.Count == 0)
        {
            return new PreviousSymbolInfo([], new Dictionary<string, Guid>(StringComparer.Ordinal));
        }

        var previousChunkRows = await chunks.ListByFileAsync(fileId, ct).ConfigureAwait(false);
        var bodyTextBySymbolId = previousChunkRows
            .Where(c => c.SymbolId.HasValue)
            .ToDictionary(c => c.SymbolId!.Value, c => c.Text);
        var qualifiedNameById = previousSymbolRows.ToDictionary(s => s.Id, s => s.QualifiedName);

        var drafts = new List<SymbolDraft>(previousSymbolRows.Count);
        var idByQualifiedName = new Dictionary<string, Guid>(previousSymbolRows.Count, StringComparer.Ordinal);

        foreach (var symbol in previousSymbolRows)
        {
            var bodyText = bodyTextBySymbolId.TryGetValue(symbol.Id, out var text) ? text : string.Empty;
            var parentQualifiedName = symbol.ParentSymbolId.HasValue
                && qualifiedNameById.TryGetValue(symbol.ParentSymbolId.Value, out var parentQn)
                    ? parentQn
                    : null;

            // SignatureText itself isn't persisted (only SignatureHash) — bodyText here is really
            // a proxy for the OLD Chunk.Text, which per SymbolDiffer.CombinedText already folds
            // signature into body wherever the parser needed to (bodyless declarations). Passing
            // it as BodyText with an empty SignatureText reproduces that same combined value when
            // SymbolDiffer.Diff recombines them for hashing — an empty SignatureText contributes
            // nothing to CombinedText, so this stays a faithful placeholder rather than a lossy one.
            drafts.Add(new SymbolDraft(
                symbol.Name, symbol.QualifiedName, symbol.Kind, symbol.StartLine, symbol.EndLine,
                SignatureText: string.Empty, BodyText: bodyText, DocComment: symbol.DocComment,
                ParentQualifiedName: parentQualifiedName));

            idByQualifiedName[symbol.QualifiedName] = symbol.Id;
        }

        return new PreviousSymbolInfo(drafts, idByQualifiedName);
    }

    private async Task EnqueueEmbedIfMissingAsync(Guid projectId, IReadOnlyList<Chunk> candidateChunks, CancellationToken ct)
    {
        if (candidateChunks.Count == 0)
        {
            return;
        }

        var hashes = candidateChunks.Select(c => c.ContentHash).Distinct().ToList();
        var missing = await embeddings.GetMissingContentHashesAsync(hashes, embeddingGenerator.GetProviderName(), ct).ConfigureAwait(false);
        if (missing.Count == 0)
        {
            return;
        }

        // One EmbedChunk job per distinct content hash (dedup: two chunks with byte-identical
        // content share one embedding-provider call), but the job's payload lists every chunk id
        // that hash covers -- EmbedChunkJobHandler upserts a vector-index record per id, since the
        // vector index is keyed by ChunkId, not ContentHash (see EmbedChunkPayload's remarks).
        var missingSet = missing.ToHashSet();
        var chunkIdsByHash = new Dictionary<string, List<Guid>>();
        foreach (var chunk in candidateChunks.Where(c => missingSet.Contains(c.ContentHash)))
        {
            if (!chunkIdsByHash.TryGetValue(chunk.ContentHash, out var ids))
            {
                chunkIdsByHash[chunk.ContentHash] = ids = [];
            }

            ids.Add(chunk.Id);
        }

        foreach (var chunk in candidateChunks)
        {
            if (!chunkIdsByHash.Remove(chunk.ContentHash, out var chunkIds))
            {
                continue; // already embedded, or an earlier chunk in this batch owns this hash's job
            }

            await jobQueue.EnqueueAsync(
                JobType.EmbedChunk, new EmbedChunkPayload(projectId, chunkIds, chunk.ContentHash, chunk.Text), ct: ct)
                .ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<SymbolDraft> DeduplicateByQualifiedName(
        IReadOnlyList<SymbolDraft> drafts, string relativePath, ILogger logger)
    {
        if (drafts.Count < 2)
        {
            return drafts;
        }

        var byQualifiedName = new Dictionary<string, SymbolDraft>(drafts.Count, StringComparer.Ordinal);
        var duplicateCount = 0;
        foreach (var draft in drafts)
        {
            if (byQualifiedName.ContainsKey(draft.QualifiedName))
            {
                duplicateCount++;
            }

            byQualifiedName[draft.QualifiedName] = draft; // last one wins, matching SymbolDiffer.Diff's own policy
        }

        if (duplicateCount > 0)
        {
            logger.LogWarning(
                "{File}: {Count} symbol(s) shared a QualifiedName with another symbol in the same file " +
                "(most commonly an overload — QualifiedName here doesn't encode the parameter list) and were " +
                "collapsed to the last-parsed declaration; only one is indexed.",
                relativePath, duplicateCount);
        }

        return [.. byQualifiedName.Values];
    }

    private static string Cap(string text, int maxChars) => text.Length > maxChars ? text[..maxChars] : text;

    private static string NormalizePath(string relativePath) => relativePath.Replace('\\', '/');

    private static string? LanguageFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".ts" or ".tsx" => "typescript",
        ".js" or ".jsx" => "javascript",
        ".cs" => "csharp",
        ".py" => "python",
        _ => null
    };

    private sealed record PreviousSymbolInfo(IReadOnlyList<SymbolDraft> Drafts, Dictionary<string, Guid> IdByQualifiedName);
}
