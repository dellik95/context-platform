using Context.Memory.Abstractions;
using Microsoft.Extensions.AI;

namespace Context.Memory.Consolidation;

internal static class MemoryVectors
{
    private const int BatchSize = 32;

    /// <summary>
    /// Vectors of <paramref name="memories"/> comparable under <paramref name="modelKey"/>. A vector from
    /// another model, or of another size than <paramref name="dimensions"/> (by default the most common
    /// size among this model's vectors), is re-embedded now and stored, so the next comparison is cheap.
    /// </summary>
    public static async Task<(Dictionary<Guid, float[]> Vectors, int Reembedded)> CurrentAsync(
        IMemoryStore store,
        IReadOnlyList<Context.Domain.Entities.Memory> memories,
        string modelKey,
        int? dimensions,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        EmbeddingGenerationOptions? options,
        CancellationToken ct)
    {
        var vectors = new Dictionary<Guid, float[]>();
        if (memories.Count == 0)
        {
            return (vectors, 0);
        }

        var stored = await store.GetEmbeddingsAsync([.. memories.Select(m => m.Id)], ct).ConfigureAwait(false);
        dimensions ??= stored.Values
            .Where(v => v.ModelKey == modelKey)
            .GroupBy(v => v.Vector.Length)
            .OrderByDescending(g => g.Count())
            .Select(g => (int?)g.Key)
            .FirstOrDefault();

        var stale = new List<Context.Domain.Entities.Memory>();
        foreach (var memory in memories)
        {
            if (stored.TryGetValue(memory.Id, out var vector) && vector.ModelKey == modelKey && vector.Vector.Length == dimensions)
            {
                vectors[memory.Id] = vector.Vector;
            }
            else
            {
                stale.Add(memory);
            }
        }

        foreach (var batch in stale.Chunk(BatchSize))
        {
            var fresh = await generator.GenerateAsync(batch.Select(MemoryEmbeddingText.For), options, ct).ConfigureAwait(false);
            for (var i = 0; i < batch.Length && i < fresh.Count; i++)
            {
                var vector = fresh[i].Vector.ToArray();
                vectors[batch[i].Id] = vector;
                await store.UpsertEmbeddingAsync(batch[i], new MemoryVector(modelKey, vector), ct).ConfigureAwait(false);
            }
        }

        return (vectors, stale.Count);
    }
}
