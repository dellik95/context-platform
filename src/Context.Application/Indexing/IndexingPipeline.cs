using Context.Application.Abstractions;
using Context.Application.Jobs;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Indexing;
using Context.Indexing.Abstractions;
using Context.Infrastructure.Abstractions;
using Context.Shared;
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
/// <see cref="Symbol"/> only persists <see cref="Symbol.BodyHash"/>, not the raw body text
/// <see cref="SymbolDiffer.Diff"/> needs to re-hash for comparison. This pipeline recovers a
/// usable proxy for a symbol's previous body text from its corresponding <see cref="Chunk.Text"/>
/// (chunks are created one-per-symbol from exactly the same body text used to compute
/// <see cref="Symbol.BodyHash"/> — see <see cref="IndexParsedFileAsync"/> — so the two stay in
/// lockstep by construction). The one edge case this can't recover from is a symbol indexed
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
    IFileScanner scanner,
    Func<string, IIgnoreMatcher> ignoreFactory,
    IEnumerable<ILanguageParser> parsers,
    IContentHasher hasher,
    IJobQueue jobQueue) : IIndexingPipeline
{
    /// <summary>Safety cap on a single chunk's text so one pathological symbol/file can't blow up storage/search.</summary>
    private const int MaxChunkChars = 20_000;

    public Task IndexFileAsync(Guid projectId, string relativePath, CancellationToken ct) =>
        IndexFileInternalAsync(projectId, relativePath, forceReprocess: false, ct);

    public async Task ReindexProjectAsync(Guid projectId, bool full, CancellationToken ct)
    {
        var project = await projects.GetByIdAsync(projectId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Project {projectId} was not found.");

        var ignore = ignoreFactory(project.RootPath);
        var scannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var scanned in scanner.ScanAsync(project.RootPath, ignore, ct).WithCancellation(ct))
        {
            scannedPaths.Add(NormalizePath(scanned.RelativePath));
            await IndexFileInternalAsync(projectId, scanned.RelativePath, forceReprocess: full, ct).ConfigureAwait(false);
        }

        // Anything still on record for this project but not found by this scan has been deleted
        // (or moved out from under an ignore rule) since the last reindex.
        var knownFiles = await files.ListByProjectAsync(projectId, includeDeleted: false, ct).ConfigureAwait(false);
        foreach (var known in knownFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (!scannedPaths.Contains(NormalizePath(known.RelativePath)))
            {
                await HandleFileRemovedAsync(projectId, known.RelativePath, ct).ConfigureAwait(false);
            }
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

        await EnqueueEmbedIfMissingAsync([chunk], ct).ConfigureAwait(false);

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
        var currentDrafts = parser.ExtractSymbols(ast);
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

            var bodyText = Cap(draft.BodyText ?? string.Empty, MaxChunkChars);
            newChunkRows.Add(new Chunk
            {
                Id = Guid.NewGuid(),
                FileId = fileEntity.Id,
                SymbolId = symbolId,
                Text = bodyText,
                TokenCount = TokenEstimator.Estimate(bodyText),
                ContentHash = hasher.HashText(bodyText)
            });
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
            .Where(c => c.SymbolId.HasValue && changedOrAddedSymbolIds.Contains(c.SymbolId.Value))
            .ToList();
        await EnqueueEmbedIfMissingAsync(chunksNeedingEmbedCheck, ct).ConfigureAwait(false);

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

            // SignatureText isn't persisted (only SignatureHash) and SymbolDiffer.Diff never
            // reads it — only QualifiedName (key) and a hash of BodyText drive its decision — so
            // an empty placeholder here is safe.
            drafts.Add(new SymbolDraft(
                symbol.Name, symbol.QualifiedName, symbol.Kind, symbol.StartLine, symbol.EndLine,
                SignatureText: string.Empty, BodyText: bodyText, DocComment: symbol.DocComment,
                ParentQualifiedName: parentQualifiedName));

            idByQualifiedName[symbol.QualifiedName] = symbol.Id;
        }

        return new PreviousSymbolInfo(drafts, idByQualifiedName);
    }

    private async Task EnqueueEmbedIfMissingAsync(IReadOnlyList<Chunk> candidateChunks, CancellationToken ct)
    {
        if (candidateChunks.Count == 0)
        {
            return;
        }

        var hashes = candidateChunks.Select(c => c.ContentHash).Distinct().ToList();
        var missing = await embeddings.GetMissingContentHashesAsync(hashes, ct).ConfigureAwait(false);
        if (missing.Count == 0)
        {
            return;
        }

        var missingSet = missing.ToHashSet();
        var alreadyEnqueued = new HashSet<string>();

        foreach (var chunk in candidateChunks)
        {
            if (!missingSet.Contains(chunk.ContentHash) || !alreadyEnqueued.Add(chunk.ContentHash))
            {
                continue; // already embedded, or an earlier chunk in this batch has identical content
            }

            await jobQueue.EnqueueAsync(JobType.EmbedChunk, new EmbedChunkPayload(chunk.Id, chunk.ContentHash, chunk.Text), ct: ct)
                .ConfigureAwait(false);
        }
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
