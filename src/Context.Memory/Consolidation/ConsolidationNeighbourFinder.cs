using Context.Memory.Abstractions;
using Context.Shared;
using Microsoft.Extensions.AI;

namespace Context.Memory.Consolidation;

/// <summary>
/// Finds the nearest live candidate neighbours for an incoming memory across strong entities,
/// topic key, and vector similarity, and harmonizes topics across related neighbours.
/// </summary>
internal sealed class ConsolidationNeighbourFinder(IMemoryStore store)
{
    private const int MaxEntityNeighbours = 10;
    private const int MaxTopicNeighbours = 10;
    private const int SimilaritySearchLimit = 16;
    private const int MaxSimilarNeighbours = 8;

    public async Task<IReadOnlyList<ConsolidationNeighbour>> FindAsync(
        IncomingMemory incoming,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        EmbeddingGenerationOptions? embeddingOptions,
        int limit,
        CancellationToken ct)
    {
        var query = incoming.Embedding!;
        var candidateStrong = EntityExtractor.StrongKeys(incoming.Entities.Select(e => e.Key));
        var ids = new HashSet<Guid>();

        if (candidateStrong.Count > 0)
        {
            var matches = await store.FindLiveKnowledgeEntityMatchesAsync(incoming.ProjectId, candidateStrong, ct).ConfigureAwait(false);
            ids.UnionWith(matches
                .GroupBy(m => m.MemoryId)
                .OrderByDescending(g => g.Count())
                .Take(MaxEntityNeighbours)
                .Select(g => g.Key));
        }

        if (incoming.TopicKey is { } topicKey)
        {
            var sameTopic = await store.ListLiveByTopicKeyAsync(incoming.ProjectId, topicKey, ct).ConfigureAwait(false);
            ids.UnionWith(sameTopic.Take(MaxTopicNeighbours).Select(m => m.Id));
        }

        var similar = await store.SearchSimilarAsync(incoming.ProjectId, query, SimilaritySearchLimit, ct).ConfigureAwait(false);
        ids.UnionWith(similar
            .Where(s => s.Score >= MemoryConsolidator.NeighbourMinCosine)
            .OrderByDescending(s => s.Score)
            .Take(MaxSimilarNeighbours)
            .Select(s => s.MemoryId));

        if (ids.Count == 0)
        {
            return [];
        }

        var memories = (await store.GetByIdsAsync(ids, ct).ConfigureAwait(false))
            .Where(m => m.IsLive && m.ProjectId == incoming.ProjectId)
            .ToList();
        if (memories.Count == 0)
        {
            return [];
        }

        var memoryIds = memories.Select(m => m.Id).ToList();
        var (vectors, _) = await MemoryVectors.CurrentAsync(
            store, memories, query.ModelKey, query.Vector.Length, generator, embeddingOptions, ct).ConfigureAwait(false);
        var entityRows = (await store.ListEntitiesAsync(memoryIds, ct).ConfigureAwait(false)).ToLookup(r => r.MemoryId);

        return [.. memories
            .Select(m =>
            {
                var rows = entityRows[m.Id].ToList();
                var strong = EntityExtractor.StrongKeys(rows.Count > 0 ? rows.Select(r => r.Key) : EntityExtractor.Extract(m).Select(e => e.Key));
                var cosine = vectors.TryGetValue(m.Id, out var vector) ? CosineSimilarity.Compute(query.Vector, vector) : 0.0;
                var topicMatch = incoming.TopicKey is not null && incoming.TopicKey == m.TopicKey ? 1.0 : 0.0;
                var score = 0.5 * cosine + 0.3 * TextTokens.Jaccard(candidateStrong, strong) + 0.2 * topicMatch;
                return new ConsolidationNeighbour(m, cosine, strong, score);
            })
            .OrderByDescending(n => n.Score)
            .Take(limit)];
    }

    public static IncomingMemory AdoptTopic(IncomingMemory incoming, IReadOnlyList<ConsolidationNeighbour> neighbours)
    {
        if (incoming.TopicKey is { } key && neighbours.FirstOrDefault(n => n.Memory.TopicKey == key) is { } sameKey)
        {
            return incoming with { Topic = sameKey.Memory.Topic ?? incoming.Topic };
        }

        var candidateStrong = EntityExtractor.StrongKeys(incoming.Entities.Select(e => e.Key));
        var sharing = neighbours.FirstOrDefault(n => n.Memory.TopicKey is not null && n.StrongKeys.Count(candidateStrong.Contains) >= 2);
        return sharing is null ? incoming : incoming with { Topic = sharing.Memory.Topic, TopicKey = sharing.Memory.TopicKey };
    }
}
