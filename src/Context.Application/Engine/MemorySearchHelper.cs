using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Context.Memory.Decay;
using Context.Memory.Search;
using Context.Providers;
using Context.Providers.Abstractions;
using Context.Providers.Embeddings;
using Context.Retrieval.Scoring;
using Context.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Context.Application.Engine;

/// <summary>
/// Shared hybrid (semantic + lexical + entity) memory matcher used by both
/// <see cref="MemoryCoordinator"/> and <see cref="ContextPackDeliveryCoordinator"/>.
/// </summary>
internal sealed class MemorySearchHelper(
    IMemoryRepository memoryRepository,
    IMemoryStore memoryStore,
    IProviderResolver providerResolver,
    ILogger? logger)
{
    private const double MemorySemanticMinSimilarity = 0.50;
    private const double MemoryLexicalBoost = 0.10;
    private const double MemoryEntityBoost = 0.10;
    private const double MemoryRelevanceTieBreak = 0.03;
    private const double MemoryTypeTieBreak = 0.02;

    internal static readonly EmbeddingGenerationOptions InteractiveEmbeddingOptions = new()
    {
        AdditionalProperties = new AdditionalPropertiesDictionary { [OllamaEmbeddingGeneratorDecorator.InteractiveRequestKey] = true }
    };

    public async Task<Dictionary<Guid, double>> MatchAsync(
        Guid projectId, MemoryQuery query, IReadOnlyList<Context.Domain.Entities.Memory> pool, CancellationToken ct)
    {
        var matches = new Dictionary<Guid, double>();
        if (!query.HasSubject)
        {
            return matches;
        }

        var entities = await EntityMatchesAsync(projectId, query, ct).ConfigureAwait(false);
        var similarities = await SemanticSimilaritiesAsync(projectId, query.Text, pool, ct).ConfigureAwait(false);
        foreach (var memory in pool)
        {
            var lexical = query.LexicalMatch(memory);
            var entity = entities.GetValueOrDefault(memory.Id);
            if (similarities is null)
            {
                if (lexical > 0 || entity > 0)
                {
                    matches[memory.Id] = Math.Max(lexical, entity);
                }

                continue;
            }

            var similarity = similarities.GetValueOrDefault(memory.Id);
            if (similarity >= MemorySemanticMinSimilarity || entity > 0 || lexical >= 1)
            {
                matches[memory.Id] = similarity + (MemoryLexicalBoost * lexical) + (MemoryEntityBoost * entity);
            }
        }

        return matches;
    }

    public static double Score(Context.Domain.Entities.Memory memory, double match, DateTimeOffset now) =>
        match
        + (MemoryRelevanceTieBreak * DecayCalculator.ComputeRelevanceMultiplier(memory.Confidence, memory.LastReinforcedAt, memory.Pinned, memory.Tier, now))
        + (MemoryTypeTieBreak * HybridScorer.NormalizedMemoryTypeBonus(memory.Type));

    private async Task<Dictionary<Guid, double>> EntityMatchesAsync(Guid projectId, MemoryQuery query, CancellationToken ct)
    {
        if (query.EntityKeys.Count == 0)
        {
            return [];
        }

        var entities = await memoryRepository.ListLiveEntitiesByKeysAsync(projectId, [.. query.EntityKeys], ct).ConfigureAwait(false);
        return entities
            .GroupBy(e => e.MemoryId)
            .ToDictionary(g => g.Key, g => (double)g.Select(e => e.Key).Distinct(StringComparer.Ordinal).Count() / query.EntityKeys.Count);
    }

    private async Task<Dictionary<Guid, double>?> SemanticSimilaritiesAsync(
        Guid projectId, string query, IReadOnlyList<Context.Domain.Entities.Memory> pool, CancellationToken ct)
    {
        IEmbeddingGenerator<string, Embedding<float>> generator;
        float[] queryVector;
        try
        {
            generator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
            if (generator.IsNoOp())
            {
                return null;
            }

            queryVector = (await generator.GenerateAsync(query, InteractiveEmbeddingOptions, ct).ConfigureAwait(false)).Vector.ToArray();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Embedding a memory query failed for project {ProjectId}; memory search falls back to lexical matching.", projectId);
            return null;
        }

        var modelKey = generator.GetProviderModelKey();
        var embeddings = await memoryStore.GetEmbeddingsAsync([.. pool.Select(m => m.Id)], ct).ConfigureAwait(false);
        var similarities = embeddings
            .Where(e => e.Value.ModelKey == modelKey)
            .ToDictionary(e => e.Key, e => CosineSimilarity.Compute(queryVector, e.Value.Vector));
        if (similarities.Count == 0)
        {
            logger?.LogWarning(
                "No memory of project {ProjectId} is embedded by {ModelKey}; memory search falls back to lexical matching until embeddings are recreated.",
                projectId, modelKey);
            return null;
        }

        return similarities;
    }
}
