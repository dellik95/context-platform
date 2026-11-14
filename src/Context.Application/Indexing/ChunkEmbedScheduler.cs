using Context.Application.Jobs;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Providers;
using Context.Providers.Abstractions;
using Context.Providers.Embeddings;

namespace Context.Application.Indexing;

/// <summary>
/// Batches and enqueues <see cref="JobType.EmbedChunk"/> jobs only for chunk content hashes that
/// do not yet have an embedding under the project's active embedding model.
/// </summary>
internal sealed class ChunkEmbedScheduler(
    IEmbeddingRepository embeddings,
    IProviderResolver providerResolver,
    IJobQueue jobQueue)
{
    public async Task EnqueueMissingAsync(Guid projectId, IReadOnlyList<Chunk> candidateChunks, CancellationToken ct)
    {
        if (candidateChunks.Count == 0)
        {
            return;
        }

        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
        var hashes = candidateChunks.Select(c => c.ContentHash).Distinct().ToList();
        var missing = await embeddings.GetMissingContentHashesAsync(hashes, embeddingGenerator.GetProviderModelKey(), ct).ConfigureAwait(false);
        if (missing.Count == 0)
        {
            return;
        }

        var missingSet = missing.ToHashSet();
        var items = candidateChunks
            .Where(c => missingSet.Contains(c.ContentHash))
            .GroupBy(c => c.ContentHash)
            .Select(g => new EmbedChunkItem(g.Key, g.First().Text, [.. g.Select(c => c.Id)]));

        foreach (var batch in embeddingGenerator.GetBatchLimits().Split(items, i => i.Text))
        {
            await jobQueue.EnqueueAsync(JobType.EmbedChunk, new EmbedChunkPayload(projectId, batch), projectId, ct: ct)
                .ConfigureAwait(false);
        }
    }
}
