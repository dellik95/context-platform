using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using Context.Providers;
using Context.Providers.Abstractions;
using Context.Providers.Embeddings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Context.Memory.Consolidation;

/// <summary>
/// Default <see cref="IMemoryConsolidator"/>. Coordinates neighbour discovery, topic adoption,
/// code checks, relation judgement, and transactional consolidation persistence.
/// </summary>
public sealed class MemoryConsolidator(
    IMemoryStore store,
    ICodeVerifier codeVerifier,
    IProviderResolver providerResolver,
    IUsageRecorder usageRecorder,
    IMemoryMergeOptionsProvider mergeOptionsProvider,
    ILogger<MemoryConsolidator>? logger = null) : IMemoryConsolidator
{
    public const int MaxNeighbours = 5;
    public const double NeighbourMinCosine = 0.60;
    public const double FastDuplicateCosine = 0.97;

    private static readonly EmbeddingGenerationOptions InteractiveOptions = new()
    {
        AdditionalProperties = new AdditionalPropertiesDictionary { [OllamaEmbeddingGeneratorDecorator.InteractiveRequestKey] = true }
    };

    private readonly MemoryCodeChecker _codeChecker = new(codeVerifier, logger);
    private readonly ConsolidationNeighbourFinder _neighbourFinder = new(store);
    private readonly CandidateRelationAssessor _assessor = new(providerResolver, usageRecorder, logger);
    private readonly ConsolidationOutcomeWriter _outcomes = new(store, logger);

    public async Task<ConsolidationResult> ConsolidateAsync(
        Guid projectId,
        Guid? sessionId,
        MemoryCandidate candidate,
        bool isInteractive,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var normalized = MemoryCandidateValidator.Normalize(candidate, out var reason)
            ?? throw new ArgumentException($"Memory candidate rejected: {reason}.", nameof(candidate));

        var now = DateTimeOffset.UtcNow;
        var incoming = new IncomingMemory(
            projectId,
            sessionId,
            normalized,
            EntityExtractor.Extract(normalized),
            normalized.Topic,
            TopicNormalizer.Normalize(normalized.Topic),
            now,
            normalized.ObservedAt is { } observedAt && observedAt < now ? observedAt : now);

        return await PlaceKnowledgeAsync(incoming, isInteractive ? InteractiveOptions : null, ct).ConfigureAwait(false);
    }

    private async Task<ConsolidationResult> PlaceKnowledgeAsync(
        IncomingMemory incoming, EmbeddingGenerationOptions? options, CancellationToken ct)
    {
        var generator = await providerResolver.GetEmbeddingGeneratorAsync(incoming.ProjectId, ct).ConfigureAwait(false);
        var embedding = await generator.GenerateAsync(MemoryEmbeddingText.For(incoming.Candidate), options, ct).ConfigureAwait(false);
        incoming = incoming with { Embedding = new MemoryVector(generator.GetProviderModelKey(), embedding.Vector.ToArray()) };

        var mergeOptions = await mergeOptionsProvider.GetAsync(incoming.ProjectId, ct).ConfigureAwait(false);
        var neighbours = await _neighbourFinder
            .FindAsync(incoming, generator, options, Math.Max(MaxNeighbours, mergeOptions.OnlineMaxSources), ct)
            .ConfigureAwait(false);
        incoming = ConsolidationNeighbourFinder.AdoptTopic(incoming, neighbours);
        incoming = incoming with { CodeCheck = await _codeChecker.CheckAsync(incoming.ProjectId, incoming.Candidate, incoming.Entities, ct).ConfigureAwait(false) };

        if (neighbours.Count == 0)
        {
            return await _outcomes.InsertNewAsync(incoming, ct).ConfigureAwait(false);
        }

        var (relation, neighbour, reason, complements) = await _assessor.JudgeAsync(incoming, neighbours, ct).ConfigureAwait(false);
        if (neighbour is null)
        {
            return await _outcomes.InsertNewAsync(incoming, ct).ConfigureAwait(false);
        }

        if (relation == MemoryRelation.Complements)
        {
            var merger = new OnlineMergeExecutor(store, providerResolver, usageRecorder, _outcomes, logger);
            return mergeOptions.OnlineMergeEnabled
                ? await merger.MergeAsync(incoming, complements, reason, mergeOptions, generator, options, ct).ConfigureAwait(false)
                : await _outcomes.InsertAlongsideAsync(incoming, neighbour.Memory, ct).ConfigureAwait(false);
        }

        var targetCheck = relation is MemoryRelation.Supersedes or MemoryRelation.Contradicts
            ? await _codeChecker.CheckAsync(neighbour.Memory, ct).ConfigureAwait(false)
            : CodeCheck.Unknown;

        return await _outcomes.ApplyVerdictInTransactionAsync(incoming, neighbour, relation, reason, targetCheck, ct).ConfigureAwait(false);
    }
}
