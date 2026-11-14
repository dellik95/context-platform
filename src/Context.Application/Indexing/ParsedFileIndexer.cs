using System.Text.Json;
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
/// symbol IDs, updating chunks and optional file summaries, and scheduling embedding/graph updates.
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

    public async Task IndexAsync(
        Guid projectId, DomainFile fileEntity, ILanguageParser parser, string sourceText, bool isFirstIndex, CancellationToken ct)
    {
        var previous = await LoadPreviousSymbolDraftsAsync(fileEntity.Id, ct).ConfigureAwait(false);

        var ast = parser.Parse(sourceText);
        var currentDrafts = DeduplicateByQualifiedName(parser.ExtractSymbols(ast), fileEntity.RelativePath, logger);
        var diff = SymbolDiffer.Diff(previous.Drafts, currentDrafts);

        var idByQualifiedName = AssignStableIds(currentDrafts, previous.IdByQualifiedName);
        var (newSymbolRows, newChunkRows) = BuildSymbolAndChunkRows(fileEntity.Id, currentDrafts, idByQualifiedName);

        var summaryChunk = await TrySummarizeFileAsync(projectId, fileEntity, currentDrafts, ct).ConfigureAwait(false);
        if (summaryChunk is not null)
        {
            newChunkRows.Add(summaryChunk);
        }

        // FK ordering: clear old chunks -> replace symbols -> insert new chunks.
        await chunks.ReplaceForFileAsync(fileEntity.Id, [], ct).ConfigureAwait(false);
        await symbols.ReplaceForFileAsync(fileEntity.Id, newSymbolRows, ct).ConfigureAwait(false);
        await chunks.ReplaceForFileAsync(fileEntity.Id, newChunkRows, ct).ConfigureAwait(false);

        var changedOrAddedSymbolIds = diff.Added.Concat(diff.Changed)
            .Select(d => idByQualifiedName[d.QualifiedName])
            .ToHashSet();
        var chunksNeedingEmbedCheck = newChunkRows
            .Where(c => isFirstIndex || (c.SymbolId.HasValue && changedOrAddedSymbolIds.Contains(c.SymbolId.Value)) || c == summaryChunk)
            .ToList();
        await embedScheduler.EnqueueMissingAsync(projectId, chunksNeedingEmbedCheck, ct).ConfigureAwait(false);

        var hasStructuralChange = diff.Added.Count > 0 || diff.Changed.Count > 0 || diff.Removed.Count > 0;
        if (hasStructuralChange || isFirstIndex)
        {
            var relationDraftsJson = await ExtractRelationDraftsJsonAsync(projectId, fileEntity.Id, parser, sourceText, ct).ConfigureAwait(false);
            await jobQueue.EnqueueAsync(
                    JobType.UpdateGraph,
                    new UpdateGraphPayload(projectId, fileEntity.Id, Invalidate: false, relationDraftsJson),
                    projectId, ct: ct)
                .ConfigureAwait(false);
        }
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

    private (List<Symbol> Symbols, List<Chunk> Chunks) BuildSymbolAndChunkRows(
        Guid fileId, IReadOnlyList<SymbolDraft> drafts, Dictionary<string, Guid> idByQualifiedName)
    {
        var newSymbolRows = new List<Symbol>(drafts.Count);
        var newChunkRows = new List<Chunk>(drafts.Count);

        foreach (var draft in drafts)
        {
            var symbolId = idByQualifiedName[draft.QualifiedName];
            var parentId = draft.ParentQualifiedName is not null
                && idByQualifiedName.TryGetValue(draft.ParentQualifiedName, out var pid)
                    ? pid
                    : (Guid?)null;

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
                SignatureHash = hasher.HashText(draft.SignatureText ?? string.Empty),
                BodyHash = hasher.HashText(draft.BodyText ?? string.Empty),
                DocComment = draft.DocComment
            });

            var combined = SymbolDiffer.CombinedText(draft.DocComment, draft.SignatureText, draft.BodyText);
            var chunkText = combined.Length > MaxChunkChars ? combined[..MaxChunkChars] : combined;
            newChunkRows.Add(new Chunk
            {
                Id = Guid.NewGuid(),
                FileId = fileId,
                SymbolId = symbolId,
                Text = chunkText,
                TokenCount = TokenEstimator.Estimate(chunkText),
                ContentHash = hasher.HashText(chunkText)
            });
        }

        return (newSymbolRows, newChunkRows);
    }

    private async Task<Chunk?> TrySummarizeFileAsync(
        Guid projectId, DomainFile fileEntity, IReadOnlyList<SymbolDraft> drafts, CancellationToken ct)
    {
        var summary = await fileSummarizer.SummarizeAsync(projectId, fileEntity.RelativePath, drafts, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        fileEntity.Summary = summary;
        var persistedFile = await files.UpsertAsync(fileEntity, ct).ConfigureAwait(false);

        var summaryText = summary.Length > MaxChunkChars ? summary[..MaxChunkChars] : summary;
        return new Chunk
        {
            Id = Guid.NewGuid(),
            FileId = persistedFile.Id,
            SymbolId = null,
            Text = summaryText,
            TokenCount = TokenEstimator.Estimate(summaryText),
            ContentHash = hasher.HashText(summaryText)
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

            drafts.Add(new SymbolDraft(
                symbol.Name, symbol.QualifiedName, symbol.Kind, symbol.StartLine, symbol.EndLine,
                SignatureText: string.Empty, BodyText: bodyText, DocComment: symbol.DocComment,
                ParentQualifiedName: parentQualifiedName));

            idByQualifiedName[symbol.QualifiedName] = symbol.Id;
        }

        return new PreviousSymbolInfo(drafts, idByQualifiedName);
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

            byQualifiedName[draft.QualifiedName] = draft;
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

    private sealed record PreviousSymbolInfo(IReadOnlyList<SymbolDraft> Drafts, Dictionary<string, Guid> IdByQualifiedName);
}
