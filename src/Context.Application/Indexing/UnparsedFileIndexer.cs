using Context.Application.Jobs;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Indexing.Abstractions;
using Context.Infrastructure.Abstractions;
using Context.Shared;
using Microsoft.Extensions.DataIngestion;
using Microsoft.Extensions.DataIngestion.Chunkers;
using Microsoft.ML.Tokenizers;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Application.Indexing;

/// <summary>
/// Indexes files that have no AST language parser (e.g. Markdown, JSON, configs) using overlapping
/// token-window chunking so large documents remain granular for semantic and lexical retrieval.
/// </summary>
internal sealed class UnparsedFileIndexer(
    ISymbolRepository symbols,
    IChunkRepository chunks,
    IContentHasher hasher,
    IJobQueue jobQueue,
    ChunkEmbedScheduler embedScheduler)
{
    private const int MaxUnparsedDocChars = 200_000;
    private const int UnparsedChunkMaxTokens = 512;
    private const int UnparsedChunkOverlapTokens = 64;

    private static readonly Lazy<Tokenizer> TokenizerInstance = new(() => TiktokenTokenizer.CreateForModel("gpt-4o"));

    public async Task IndexAsync(Guid projectId, DomainFile fileEntity, string sourceText, bool isFirstIndex, CancellationToken ct)
    {
        var text = sourceText.Length > MaxUnparsedDocChars ? sourceText[..MaxUnparsedDocChars] : sourceText;
        var chunkTexts = await SplitTextAsync(fileEntity.RelativePath, text, ct).ConfigureAwait(false);

        var chunkEntities = chunkTexts.Select(chunkText => new Chunk
        {
            Id = Guid.NewGuid(),
            FileId = fileEntity.Id,
            SymbolId = null,
            Text = chunkText,
            TokenCount = TokenEstimator.Estimate(chunkText),
            ContentHash = hasher.HashText(chunkText)
        }).ToList();

        await symbols.ReplaceForFileAsync(fileEntity.Id, [], ct).ConfigureAwait(false);
        await chunks.ReplaceForFileAsync(fileEntity.Id, chunkEntities, ct).ConfigureAwait(false);

        await embedScheduler.EnqueueMissingAsync(projectId, chunkEntities, ct).ConfigureAwait(false);

        if (isFirstIndex)
        {
            await jobQueue.EnqueueAsync(JobType.UpdateGraph, new UpdateGraphPayload(projectId, fileEntity.Id, Invalidate: false), projectId, ct: ct)
                .ConfigureAwait(false);
        }
    }

    private static async Task<IReadOnlyList<string>> SplitTextAsync(string identifier, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [text];
        }

        var document = new IngestionDocument(identifier);
        var section = new IngestionDocumentSection(text);
        section.Elements.Add(new IngestionDocumentParagraph(text));
        document.Sections.Add(section);

        var options = new IngestionChunkerOptions(TokenizerInstance.Value)
        {
            MaxTokensPerChunk = UnparsedChunkMaxTokens,
            OverlapTokens = UnparsedChunkOverlapTokens,
        };
        var chunker = new DocumentTokenChunker(options);

        var result = new List<string>();
        await foreach (var chunk in chunker.ProcessAsync(document, ct).ConfigureAwait(false))
        {
            if (!string.IsNullOrWhiteSpace(chunk.Content))
            {
                result.Add(chunk.Content);
            }
        }

        return result.Count > 0 ? result : [text];
    }
}
