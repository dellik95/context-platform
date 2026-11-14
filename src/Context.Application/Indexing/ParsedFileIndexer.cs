using System.Text.Json;
using Context.Application.Adapters;
using Context.Application.Jobs;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Indexing;
using Context.Indexing.Abstractions;
using Context.Infrastructure.Abstractions;
using Context.Shared;
using Microsoft.Extensions.Logging;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Application.Indexing;

/// <summary>
/// Indexes AST-supported source files by diffing against previous symbol snapshots, preserving
/// symbol and unchanged-chunk IDs, updating chunks and optional file summaries, and scheduling
/// embedding/graph updates.
/// </summary>
internal sealed class ParsedFileIndexer(
    IFileRepository files,
    ISymbolRepository symbols,
    IChunkRepository chunks,
    IContentHasher hasher,
    IJobQueue jobQueue,
    ChunkEmbedScheduler embedScheduler,
    IFileSummarizer fileSummarizer,
    ILogger logger)
{
    private const int MaxChunkChars = 20_000;

    /// <summary>
    /// Indexes <paramref name="fileEntity"/> and returns <see langword="true"/> when a graph
    /// update is needed for this file. When <paramref name="deferGraphUpdate"/> is <see langword="false"/>,
    /// the <c>UpdateGraph</c> job is enqueued immediately; callers running a full-project reindex pass
    /// set <paramref name="deferGraphUpdate"/> to <see langword="true"/> and enqueue graph updates
    /// once all files' symbols have been committed so cross-file edges aren't starved by file order.
    /// </summary>
    public async Task<bool> IndexAsync(
        Guid projectId, DomainFile fileEntity, ILanguageParser parser, string sourceText, bool isFirstIndex,
        CancellationToken ct, bool deferGraphUpdate = false)
    {
        var previous = await LoadPreviousSnapshotAsync(fileEntity.Id, ct).ConfigureAwait(false);

        var ast = parser.Parse(sourceText);
        var currentDrafts = DisambiguateOverloads(parser.ExtractSymbols(ast), fileEntity.RelativePath, logger);

        var idByQualifiedName = AssignStableIds(currentDrafts, previous.IdByQualifiedName);
        var (newSymbolRows, newChunkRows, changedOrAddedSymbolIds, hasStructuralChange) =
            BuildSymbolAndChunkRows(fileEntity.Id, currentDrafts, idByQualifiedName, previous);

        var summaryChunk = await TrySummarizeFileAsync(projectId, fileEntity, currentDrafts, previous.FileSummaryChunkId, previous.FileSummaryContentHash, ct)
            .ConfigureAwait(false);
        if (summaryChunk is not null)
        {
            newChunkRows.Add(summaryChunk);
        }

        // FK ordering: clear old chunks -> replace symbols -> insert new chunks. Defer sqlite-vec
        // pruning across those three steps so reused unchanged Chunk IDs keep their existing vectors.
        var pruningScope = (chunks as VectorPruningChunkRepository)?.BeginFileUpdate(fileEntity.Id);
        try
        {
            await chunks.ReplaceForFileAsync(fileEntity.Id, [], ct).ConfigureAwait(false);
            await symbols.ReplaceForFileAsync(fileEntity.Id, newSymbolRows, ct).ConfigureAwait(false);
            await chunks.ReplaceForFileAsync(fileEntity.Id, newChunkRows, ct).ConfigureAwait(false);

            if (pruningScope is not null)
            {
                await pruningScope.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (pruningScope is not null)
            {
                await pruningScope.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        var chunksNeedingEmbedCheck = newChunkRows
            .Where(c => isFirstIndex
                || (c.SymbolId.HasValue && changedOrAddedSymbolIds.Contains(c.SymbolId.Value))
                || (c == summaryChunk && !string.Equals(summaryChunk.ContentHash, previous.FileSummaryContentHash, StringComparison.Ordinal)))
            .ToList();
        await embedScheduler.EnqueueMissingAsync(projectId, chunksNeedingEmbedCheck, ct).ConfigureAwait(false);

        var needsGraphUpdate = hasStructuralChange || isFirstIndex;
        if (needsGraphUpdate && !deferGraphUpdate)
        {
            var relationDraftsJson = await ExtractRelationDraftsJsonAsync(projectId, fileEntity.Id, parser, sourceText, ct).ConfigureAwait(false);
            await jobQueue.EnqueueAsync(
                    JobType.UpdateGraph,
                    new UpdateGraphPayload(projectId, fileEntity.Id, Invalidate: false, relationDraftsJson),
                    projectId, ct: ct)
                .ConfigureAwait(false);
        }

        return needsGraphUpdate;
    }

    private static Dictionary<string, Guid> AssignStableIds(
        IReadOnlyList<SymbolDraft> drafts, Dictionary<string, Guid> previousIds)
    {
        var map = new Dictionary<string, Guid>(drafts.Count, StringComparer.Ordinal);
        foreach (var draft in drafts)
        {
            map[draft.QualifiedName] = previousIds.TryGetValue(draft.QualifiedName, out var previousId)
                ? previousId
                : Guid.NewGuid();
        }

        return map;
    }

    private (List<Symbol> Symbols, List<Chunk> Chunks, HashSet<Guid> ChangedOrAddedSymbolIds, bool HasStructuralChange) BuildSymbolAndChunkRows(
        Guid fileId,
        IReadOnlyList<SymbolDraft> drafts,
        Dictionary<string, Guid> idByQualifiedName,
        PreviousSnapshot previous)
    {
        var newSymbolRows = new List<Symbol>(drafts.Count);
        var newChunkRows = new List<Chunk>(drafts.Count);
        var changedOrAddedSymbolIds = new HashSet<Guid>();
        var seenPreviousQualifiedNames = new HashSet<string>(StringComparer.Ordinal);
        var hasStructuralChange = false;

        foreach (var draft in drafts)
        {
            var symbolId = idByQualifiedName[draft.QualifiedName];
            var parentId = draft.ParentQualifiedName is not null
                && idByQualifiedName.TryGetValue(draft.ParentQualifiedName, out var pid)
                    ? pid
                    : (Guid?)null;

            var signatureHash = hasher.HashText(draft.SignatureText ?? string.Empty);
            var bodyHash = hasher.HashText(draft.BodyText ?? string.Empty);

            newSymbolRows.Add(new Symbol
            {
                Id = symbolId,
                FileId = fileId,
                ParentSymbolId = parentId,
                Kind = draft.Kind,
                Name = draft.Name,
                QualifiedName = draft.QualifiedName,
                StartLine = draft.StartLine,
                EndLine = draft.EndLine,
                SignatureHash = signatureHash,
                BodyHash = bodyHash,
                DocComment = draft.DocComment
            });

            var plainDoc = DocCommentFormatter.ToPlainText(draft.DocComment);
            var combined = SymbolDiffer.CombinedText(plainDoc, draft.SignatureText, draft.BodyText);
            var chunkText = combined.Length > MaxChunkChars ? combined[..MaxChunkChars] : combined;
            var contentHash = hasher.HashText(chunkText);

            var chunkId = previous.ChunkIdBySymbolAndHash.TryGetValue((symbolId, contentHash), out var existingChunkId)
                ? existingChunkId
                : Guid.NewGuid();

            newChunkRows.Add(new Chunk
            {
                Id = chunkId,
                FileId = fileId,
                SymbolId = symbolId,
                Text = chunkText,
                TokenCount = TokenEstimator.Estimate(chunkText),
                ContentHash = contentHash
            });

            if (previous.ByQualifiedName.TryGetValue(draft.QualifiedName, out var prevSymbol))
            {
                seenPreviousQualifiedNames.Add(draft.QualifiedName);

                var sameMetadata = prevSymbol.Kind == draft.Kind
                    && string.Equals(prevSymbol.ParentQualifiedName, draft.ParentQualifiedName, StringComparison.Ordinal);
                var sameContent = (prevSymbol.SignatureHash == signatureHash
                        && prevSymbol.BodyHash == bodyHash
                        && string.Equals(prevSymbol.DocComment, draft.DocComment, StringComparison.Ordinal))
                    || (prevSymbol.ChunkText is not null && string.Equals(prevSymbol.ChunkText, chunkText, StringComparison.Ordinal));

                if (!sameMetadata || !sameContent)
                {
                    changedOrAddedSymbolIds.Add(symbolId);
                    hasStructuralChange = true;
                }
            }
            else
            {
                changedOrAddedSymbolIds.Add(symbolId);
                hasStructuralChange = true;
            }
        }

        if (seenPreviousQualifiedNames.Count < previous.ByQualifiedName.Count)
        {
            hasStructuralChange = true;
        }

        return (newSymbolRows, newChunkRows, changedOrAddedSymbolIds, hasStructuralChange);
    }

    private async Task<Chunk?> TrySummarizeFileAsync(
        Guid projectId,
        DomainFile fileEntity,
        IReadOnlyList<SymbolDraft> drafts,
        Guid? previousSummaryChunkId,
        string? previousSummaryHash,
        CancellationToken ct)
    {
        var summary = await fileSummarizer.SummarizeAsync(projectId, fileEntity.RelativePath, drafts, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        fileEntity.Summary = summary;
        var persistedFile = await files.UpsertAsync(fileEntity, ct).ConfigureAwait(false);

        var summaryText = summary.Length > MaxChunkChars ? summary[..MaxChunkChars] : summary;
        var contentHash = hasher.HashText(summaryText);
        var chunkId = previousSummaryChunkId.HasValue && string.Equals(previousSummaryHash, contentHash, StringComparison.Ordinal)
            ? previousSummaryChunkId.Value
            : Guid.NewGuid();

        return new Chunk
        {
            Id = chunkId,
            FileId = persistedFile.Id,
            SymbolId = null,
            Text = summaryText,
            TokenCount = TokenEstimator.Estimate(summaryText),
            ContentHash = contentHash
        };
    }

    private async Task<string?> ExtractRelationDraftsJsonAsync(
        Guid projectId, Guid fileId, ILanguageParser parser, string sourceText, CancellationToken ct)
    {
        try
        {
            var projectSymbolNames = await symbols.GetAllNamesAsync(projectId, ct).ConfigureAwait(false);
            var relationAst = parser.Parse(sourceText);
            var relationDrafts = parser.ExtractRelations(relationAst, projectSymbolNames);
            return relationDrafts.Count > 0 ? JsonSerializer.Serialize(relationDrafts) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract relation drafts for file {FileId}; UpdateGraph job will re-parse from disk instead.", fileId);
            return null;
        }
    }

    private async Task<PreviousSnapshot> LoadPreviousSnapshotAsync(Guid fileId, CancellationToken ct)
    {
        var previousSymbolRows = await symbols.ListByFileAsync(fileId, ct).ConfigureAwait(false);
        var previousChunkRows = await chunks.ListByFileAsync(fileId, ct).ConfigureAwait(false);

        var chunkTextBySymbolId = new Dictionary<Guid, string>();
        var chunkIdBySymbolAndHash = new Dictionary<(Guid SymbolId, string ContentHash), Guid>();
        Guid? summaryChunkId = null;
        string? summaryContentHash = null;

        foreach (var chunk in previousChunkRows)
        {
            if (chunk.SymbolId.HasValue)
            {
                chunkTextBySymbolId.TryAdd(chunk.SymbolId.Value, chunk.Text);
                chunkIdBySymbolAndHash.TryAdd((chunk.SymbolId.Value, chunk.ContentHash), chunk.Id);
            }
            else if (summaryChunkId is null)
            {
                summaryChunkId = chunk.Id;
                summaryContentHash = chunk.ContentHash;
            }
        }

        if (previousSymbolRows.Count == 0)
        {
            return new PreviousSnapshot(
                new Dictionary<string, PreviousSymbolRecord>(StringComparer.Ordinal),
                new Dictionary<string, Guid>(StringComparer.Ordinal),
                chunkIdBySymbolAndHash,
                summaryChunkId,
                summaryContentHash);
        }

        var qualifiedNameById = previousSymbolRows.ToDictionary(s => s.Id, s => s.QualifiedName);
        var byQualifiedName = new Dictionary<string, PreviousSymbolRecord>(previousSymbolRows.Count, StringComparer.Ordinal);
        var idByQualifiedName = new Dictionary<string, Guid>(previousSymbolRows.Count, StringComparer.Ordinal);

        foreach (var symbol in previousSymbolRows)
        {
            chunkTextBySymbolId.TryGetValue(symbol.Id, out var chunkText);
            var parentQualifiedName = symbol.ParentSymbolId.HasValue
                && qualifiedNameById.TryGetValue(symbol.ParentSymbolId.Value, out var parentQn)
                    ? parentQn
                    : null;

            byQualifiedName[symbol.QualifiedName] = new PreviousSymbolRecord(
                symbol.Kind, parentQualifiedName, symbol.SignatureHash, symbol.BodyHash, symbol.DocComment, chunkText);
            idByQualifiedName[symbol.QualifiedName] = symbol.Id;
        }

        return new PreviousSnapshot(byQualifiedName, idByQualifiedName, chunkIdBySymbolAndHash, summaryChunkId, summaryContentHash);
    }

    private static IReadOnlyList<SymbolDraft> DisambiguateOverloads(
        IReadOnlyList<SymbolDraft> drafts, string relativePath, ILogger logger)
    {
        if (drafts.Count < 2)
        {
            return drafts;
        }

        var result = new List<SymbolDraft>(drafts.Count);
        var usedQualifiedNames = new HashSet<string>(drafts.Count, StringComparer.Ordinal);
        var overloadCount = 0;

        foreach (var draft in drafts)
        {
            if (usedQualifiedNames.Add(draft.QualifiedName))
            {
                result.Add(draft);
                continue;
            }

            overloadCount++;
            var baseName = draft.QualifiedName;
            var suffix = ExtractParameterListSuffix(draft.SignatureText);
            var candidate = suffix is not null ? $"{baseName}{suffix}" : baseName;
            var ordinal = 2;
            while (!usedQualifiedNames.Add(candidate))
            {
                candidate = suffix is not null ? $"{baseName}{suffix}#{ordinal}" : $"{baseName}#{ordinal}";
                ordinal++;
            }

            result.Add(draft with { QualifiedName = candidate });
        }

        if (overloadCount > 0)
        {
            logger.LogDebug(
                "{File}: disambiguated {Count} overloaded symbol(s) with signature suffixes so every overload remains searchable.",
                relativePath, overloadCount);
        }

        return result;
    }

    private static string? ExtractParameterListSuffix(string? signatureText)
    {
        if (string.IsNullOrWhiteSpace(signatureText))
        {
            return null;
        }

        var open = signatureText.IndexOf('(');
        var close = open >= 0 ? signatureText.IndexOf(')', open + 1) : -1;
        if (open < 0 || close <= open)
        {
            return null;
        }

        var inner = signatureText[(open + 1)..close];
        var collapsed = string.Join(' ', inner.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length > 80)
        {
            collapsed = collapsed[..80];
        }

        return $"({collapsed})";
    }

    private sealed record PreviousSymbolRecord(
        SymbolKind Kind,
        string? ParentQualifiedName,
        string SignatureHash,
        string BodyHash,
        string? DocComment,
        string? ChunkText);

    private sealed record PreviousSnapshot(
        Dictionary<string, PreviousSymbolRecord> ByQualifiedName,
        Dictionary<string, Guid> IdByQualifiedName,
        Dictionary<(Guid SymbolId, string ContentHash), Guid> ChunkIdBySymbolAndHash,
        Guid? FileSummaryChunkId,
        string? FileSummaryContentHash);
}
