using System.Text.Json;
using Context.Application.Abstractions;
using Context.Application.Support;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Context.Memory.Consolidation;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// Force-regenerates every embedding for a project -- chunks and memories alike -- after the
/// project's embedding provider/model changes, per <see cref="RecreateEmbeddingsPayload"/>. Unlike
/// <see cref="EmbedChunkJobHandler"/>, this never consults <see cref="IEmbeddingRepository.GetByContentHashAsync"/>
/// as a cache-hit gate: a stale <c>(ContentHash, Provider)</c> row may already exist for the new
/// provider key (e.g. a chunk re-embedded incidentally by a later <see cref="EmbedChunkJobHandler"/>
/// run before this job got to it), but the whole point of this job is to unconditionally overwrite
/// it with a freshly computed vector rather than trust whatever is already stored.
/// </summary>
public sealed class RecreateEmbeddingsJobHandler(
    IChunkRepository chunks,
    IMemoryRepository memories,
    IEmbeddingRepository embeddings,
    IChunkVectorIndex chunkVectorIndex,
    IMemoryStore memoryStore,
    IProviderResolver providerResolver) : IJobHandler
{
    public JobType Type => JobType.RecreateEmbeddings;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<RecreateEmbeddingsPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(RecreateEmbeddingsPayload)}.");

        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(payload.ProjectId, ct).ConfigureAwait(false);
        var providerKey = embeddingGenerator.GetProviderModelKey();

        var allChunks = await chunks.ListByProjectAsync(payload.ProjectId, ct).ConfigureAwait(false);
        foreach (var group in allChunks.GroupBy(c => c.ContentHash))
        {
            ct.ThrowIfCancellationRequested();

            var result = await embeddingGenerator.GenerateAsync(group.First().Text, cancellationToken: ct).ConfigureAwait(false);
            var vector = result.Vector.ToArray();

            await embeddings.UpsertAsync(new Context.Domain.Entities.Embedding
            {
                ContentHash = group.Key,
                Provider = providerKey,
                Model = embeddingGenerator.GetModelId() ?? providerKey,
                Dim = vector.Length,
                Vector = EmbeddingVectorCodec.Encode(vector),
                CreatedAt = DateTimeOffset.UtcNow
            }, ct).ConfigureAwait(false);

            foreach (var chunk in group)
            {
                await chunkVectorIndex.UpsertAsync(chunk.Id, payload.ProjectId, providerKey, vector, ct).ConfigureAwait(false);
            }
        }

        // Events are never embedded: consolidation compares only knowledge by similarity.
        var liveKnowledge = await memories.ListByProjectAsync(payload.ProjectId, includeSuperseded: false, ct).ConfigureAwait(false);
        foreach (var memory in liveKnowledge.Where(m => m.Type.IsKnowledge()))
        {
            ct.ThrowIfCancellationRequested();

            var result = await embeddingGenerator.GenerateAsync(MemoryEmbeddingText.For(memory), cancellationToken: ct).ConfigureAwait(false);
            await memoryStore.UpsertEmbeddingAsync(memory, new MemoryVector(providerKey, result.Vector.ToArray()), ct).ConfigureAwait(false);
        }
    }
}
