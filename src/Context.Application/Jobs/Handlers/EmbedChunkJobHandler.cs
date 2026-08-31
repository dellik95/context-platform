using System.Text.Json;
using Context.Application.Support;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Providers.Abstractions;

namespace Context.Application.Jobs.Handlers;

/// <summary>Computes and stores the embedding for one chunk's content, keyed by content hash.</summary>
public sealed class EmbedChunkJobHandler(IEmbeddingRepository embeddings, IEmbeddingProvider embeddingProvider) : IJobHandler
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
        if (existing is not null)
        {
            return;
        }

        var vector = await embeddingProvider.EmbedAsync(payload.Text, ct).ConfigureAwait(false);

        var embedding = new Embedding
        {
            ContentHash = payload.ContentHash,
            Provider = embeddingProvider.Name,
            Model = embeddingProvider.Name,
            Dim = vector.Length,
            Vector = EmbeddingVectorCodec.Encode(vector),
            CreatedAt = DateTimeOffset.UtcNow
        };

        await embeddings.UpsertAsync(embedding, ct).ConfigureAwait(false);
    }
}
