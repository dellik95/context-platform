using System.Text.Json;
using Context.Application.Abstractions;
using Context.Application.Support;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Providers.Abstractions;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// Computes (or reuses) the embedding for one content hash, then upserts a per-chunk record into
/// <see cref="IChunkVectorIndex"/> for every chunk that content hash covers -- see
/// <see cref="EmbedChunkPayload"/>'s remarks on why a single job can own multiple chunk ids.
/// </summary>
public sealed class EmbedChunkJobHandler(
    IEmbeddingRepository embeddings, IChunkVectorIndex vectorIndex, IEmbeddingProvider embeddingProvider) : IJobHandler
{
    public JobType Type => JobType.EmbedChunk;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<EmbedChunkPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(EmbedChunkPayload)}.");

        // Re-check under the job's own execution: another EmbedChunk job for an identical chunk
        // (same content hash, different chunk id - e.g. two symbols with byte-identical bodies)
        // may have already embedded this content since this job was enqueued.
        var existing = await embeddings.GetByContentHashAsync(payload.ContentHash, embeddingProvider.Name, ct).ConfigureAwait(false);

        float[] vector;
        if (existing is not null)
        {
            vector = EmbeddingVectorCodec.Decode(existing.Vector);
        }
        else
        {
            vector = await embeddingProvider.EmbedAsync(payload.Text, ct).ConfigureAwait(false);

            await embeddings.UpsertAsync(new Embedding
            {
                ContentHash = payload.ContentHash,
                Provider = embeddingProvider.Name,
                Model = embeddingProvider.Name,
                Dim = vector.Length,
                Vector = EmbeddingVectorCodec.Encode(vector),
                CreatedAt = DateTimeOffset.UtcNow
            }, ct).ConfigureAwait(false);
        }

        foreach (var chunkId in payload.ChunkIds)
        {
            await vectorIndex.UpsertAsync(chunkId, payload.ProjectId, embeddingProvider.Name, vector, ct).ConfigureAwait(false);
        }
    }
}
