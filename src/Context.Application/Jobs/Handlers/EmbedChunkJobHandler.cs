using System.Text.Json;
using Context.Application.Abstractions;
using Context.Application.Support;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Providers;
using Context.Providers.Abstractions;
using Context.Providers.Embeddings;
using Microsoft.Extensions.AI;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// Computes (or reuses) the embedding for every item of an <see cref="EmbedChunkPayload"/>, one
/// provider request per batch its <see cref="EmbeddingBatchLimits"/> allow, then upserts a per-chunk
/// record into <see cref="IChunkVectorIndex"/> for every chunk each item covers.
/// </summary>
/// <remarks>
/// Content already embedded for this provider is reused rather than sent again, so a retry after a
/// partial failure only re-sends what is still missing. An item too long for the model's context
/// window fails only itself: its batch is retried item by item, and the job fails at the end
/// naming whatever could not be embedded.
/// </remarks>
public sealed class EmbedChunkJobHandler(
    IEmbeddingRepository embeddings,
    IChunkVectorIndex vectorIndex,
    IProviderResolver providerResolver) : IJobHandler
{
    public JobType Type => JobType.EmbedChunk;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<EmbedChunkPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(EmbedChunkPayload)}.");

        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(payload.ProjectId, ct).ConfigureAwait(false);
        var providerKey = embeddingGenerator.GetProviderModelKey();
        var modelId = embeddingGenerator.GetModelId() ?? providerKey;

        // Another job may have embedded identical content since this one was enqueued.
        var existing = await embeddings.GetByContentHashesAsync(payload.Items.Select(i => i.ContentHash), providerKey, ct).ConfigureAwait(false);
        var pending = new List<EmbedChunkItem>();
        foreach (var item in payload.Items)
        {
            if (existing.TryGetValue(item.ContentHash, out var embedding))
            {
                await IndexAsync(item, EmbeddingVectorCodec.Decode(embedding.Vector)).ConfigureAwait(false);
            }
            else
            {
                pending.Add(item);
            }
        }

        var tooLong = new List<(EmbedChunkItem Item, EmbeddingContextLengthException Error)>();
        foreach (var batch in embeddingGenerator.GetBatchLimits().Split(pending, i => i.Text))
        {
            try
            {
                await EmbedAsync(batch).ConfigureAwait(false);
            }
            catch (EmbeddingContextLengthException ex) when (batch.Count == 1)
            {
                tooLong.Add((batch[0], ex));
            }
            catch (EmbeddingContextLengthException)
            {
                foreach (var item in batch)
                {
                    try
                    {
                        await EmbedAsync([item]).ConfigureAwait(false);
                    }
                    catch (EmbeddingContextLengthException ex)
                    {
                        tooLong.Add((item, ex));
                    }
                }
            }
        }

        if (tooLong.Count > 0)
        {
            var items = string.Join("; ", tooLong.Select(f => $"{f.Item.ContentHash} (chunks {string.Join(", ", f.Item.ChunkIds)})"));
            throw new InvalidOperationException(
                $"{tooLong.Count} of {payload.Items.Count} item(s) exceed the embedding model's context window and were not embedded: {items}.",
                tooLong[0].Error);
        }

        async Task EmbedAsync(IReadOnlyList<EmbedChunkItem> batch)
        {
            var generated = await embeddingGenerator.GenerateAsync(batch.Select(i => i.Text), cancellationToken: ct).ConfigureAwait(false);
            for (var i = 0; i < batch.Count; i++)
            {
                var vector = generated[i].Vector.ToArray();
                await embeddings.UpsertAsync(new Context.Domain.Entities.Embedding
                {
                    ContentHash = batch[i].ContentHash,
                    Provider = providerKey,
                    Model = modelId,
                    Dim = vector.Length,
                    Vector = EmbeddingVectorCodec.Encode(vector),
                    CreatedAt = DateTimeOffset.UtcNow
                }, ct).ConfigureAwait(false);
                await IndexAsync(batch[i], vector).ConfigureAwait(false);
            }
        }

        async Task IndexAsync(EmbedChunkItem item, float[] vector)
        {
            foreach (var chunkId in item.ChunkIds)
            {
                await vectorIndex.UpsertAsync(chunkId, payload.ProjectId, providerKey, vector, ct).ConfigureAwait(false);
            }
        }
    }
}
