using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using Context.Providers;
using Context.Providers.Abstractions;
using Context.Providers.Embeddings;
using Context.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Memory.Consolidation;

/// <summary>
/// Default <see cref="IMemoryConsolidator"/>. Events are recorded apart from knowledge and only deduped
/// within their session. A knowledge candidate is compared with at most <see cref="MaxNeighbours"/>
/// live neighbours found by shared strong entity, topic key or embedding similarity, and the strongest
/// verdict (duplicate, supersedes, contradicts, complements) decides whether it reinforces, replaces,
/// contests, merges into a neighbour or is inserted on its own. Each placement is written in one
/// store transaction that re-reads its target and reinforces the same claim instead if a concurrent
/// consolidation stored it meanwhile; model and embedding calls stay outside it.
/// </summary>
public sealed class MemoryConsolidator(
    IMemoryStore store,
    ICodeVerifier codeVerifier,
    IProviderResolver providerResolver,
    IUsageRecorder usageRecorder,
    ILogger<MemoryConsolidator>? logger = null) : IMemoryConsolidator
{
    public const int MaxNeighbours = 5;
    public const double NeighbourMinCosine = 0.60;
    public const double FastDuplicateCosine = 0.97;
    public const int MaxMergedFacts = 12;
    public const int MaxMergedBodyLength = 2500;

    private const int MaxEntityNeighbours = 10;
    private const int MaxTopicNeighbours = 10;
    private const int SimilaritySearchLimit = 16;
    private const int MaxSimilarNeighbours = 8;
    private const string ConcurrentDuplicateNote = "same claim stored concurrently";

    // How far back a concurrent consolidation's CreatedAt can lie: it is stamped before the model calls.
    private static readonly TimeSpan ConcurrentWindow = TimeSpan.FromHours(1);

    private static readonly EmbeddingGenerationOptions InteractiveOptions = new()
    {
        AdditionalProperties = new AdditionalPropertiesDictionary { [OllamaEmbeddingGeneratorDecorator.InteractiveRequestKey] = true }
    };

    private static readonly MemoryRelation[] VerdictPriority =
        [MemoryRelation.Duplicate, MemoryRelation.Supersedes, MemoryRelation.Contradicts, MemoryRelation.Complements];

    private readonly MemoryCodeChecker codeChecker = new(codeVerifier, logger);

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
        var incoming = new Incoming(
            projectId,
            sessionId,
            normalized,
            EntityExtractor.Extract(normalized),
            normalized.Topic,
            TopicNormalizer.Normalize(normalized.Topic),
            now,
            normalized.ObservedAt is { } observedAt && observedAt < now ? observedAt : now);

        return normalized.Type.IsEvent()
            ? await store.InTransactionAsync(() => RecordEventAsync(incoming, ct), ct).ConfigureAwait(false)
            : await PlaceKnowledgeAsync(incoming, isInteractive ? InteractiveOptions : null, ct).ConfigureAwait(false);
    }

    private async Task<ConsolidationResult> RecordEventAsync(Incoming incoming, CancellationToken ct)
    {
        if (incoming.SessionId is { } sessionId)
        {
            var titleKey = TextTokens.Normalize(incoming.Candidate.Title);
            var sameSession = await store.ListLiveEventsBySessionAsync(incoming.ProjectId, sessionId, ct).ConfigureAwait(false);
            if (sameSession.FirstOrDefault(e => TextTokens.Normalize(e.Title) == titleKey) is { } existing)
            {
                var evidence = await store.ListEvidenceAsync([existing.Id], ct).ConfigureAwait(false);
                var entityRows = await store.ListEntitiesAsync([existing.Id], ct).ConfigureAwait(false);
                List<MemoryEvidence> added = [NewEvidence(incoming, existing.Id, EvidenceRelation.Reinforced)];

                Absorb(existing, incoming.Candidate, FactMerger.Merge(EntityExtractor.ParseList(existing.FactsJson), incoming.Candidate.Facts));
                existing.ReinforcementCount++;
                existing.LastReinforcedAt = incoming.Now;
                ConfidenceModel.Recompute(existing, [.. evidence, .. added]);

                await store.UpdateAsync(existing, null, MemoryFields.UnionEntities(existing, entityRows, incoming.Entities), added, ct).ConfigureAwait(false);
                return new ConsolidationResult(existing, ConsolidationOutcome.EventRecorded);
            }
        }

        var memory = NewMemory(incoming, MemoryStatus.Active);
        List<MemoryEvidence> created = [NewEvidence(incoming, memory.Id, EvidenceRelation.Created)];
        ConfidenceModel.Recompute(memory, created);
        await store.InsertAsync(memory, null, MemoryFields.EntityRows(memory, incoming.Entities), created, ct).ConfigureAwait(false);
        return new ConsolidationResult(memory, ConsolidationOutcome.EventRecorded);
    }

    private async Task<ConsolidationResult> PlaceKnowledgeAsync(Incoming incoming, EmbeddingGenerationOptions? options, CancellationToken ct)
    {
        var generator = await providerResolver.GetEmbeddingGeneratorAsync(incoming.ProjectId, ct).ConfigureAwait(false);
        var embedding = await generator.GenerateAsync(MemoryEmbeddingText.For(incoming.Candidate), options, ct).ConfigureAwait(false);
        var embedder = new Embedder(generator, options);
        incoming = incoming with { Embedding = new MemoryVector(generator.GetProviderModelKey(), embedding.Vector.ToArray()) };

        var neighbours = await FindNeighboursAsync(incoming, embedder, ct).ConfigureAwait(false);
        incoming = AdoptTopic(incoming, neighbours);
        incoming = incoming with { CodeCheck = await codeChecker.CheckAsync(incoming.ProjectId, incoming.Candidate, incoming.Entities, ct).ConfigureAwait(false) };

        if (neighbours.Count == 0)
        {
            return await InsertNewAsync(incoming, ct).ConfigureAwait(false);
        }

        var (relation, neighbour, reason) = await JudgeAsync(incoming, neighbours, ct).ConfigureAwait(false);
        if (neighbour is null)
        {
            return await InsertNewAsync(incoming, ct).ConfigureAwait(false);
        }

        if (relation == MemoryRelation.Complements)
        {
            return await MergeAsync(incoming, neighbour.Memory.Id, reason, embedder, ct).ConfigureAwait(false);
        }

        // Model and code-index work stays outside the write transaction; the target is re-read inside it.
        var targetCheck = relation is MemoryRelation.Supersedes or MemoryRelation.Contradicts
            ? await codeChecker.CheckAsync(neighbour.Memory, ct).ConfigureAwait(false)
            : CodeCheck.Unknown;

        return await store.InTransactionAsync(async () =>
        {
            if (relation != MemoryRelation.Duplicate
                && await FindConcurrentDuplicateAsync(incoming, ct).ConfigureAwait(false) is { } duplicate)
            {
                return await ReinforceAsync(incoming, duplicate, ConcurrentDuplicateNote, ct).ConfigureAwait(false);
            }

            if (await ReloadLiveAsync(neighbour.Memory.Id, ct).ConfigureAwait(false) is not { } target)
            {
                return await InsertNewAsync(incoming, ct).ConfigureAwait(false);
            }

            return relation switch
            {
                MemoryRelation.Duplicate => await ReinforceAsync(incoming, target, reason, ct).ConfigureAwait(false),
                MemoryRelation.Supersedes => await TrySupersedeAsync(incoming, target, targetCheck, reason, ct).ConfigureAwait(false),
                MemoryRelation.Contradicts => await ContradictAsync(incoming, target, targetCheck, reason, ct).ConfigureAwait(false),
                _ => await InsertNewAsync(incoming, ct).ConfigureAwait(false),
            };
        }, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Neighbour>> FindNeighboursAsync(Incoming incoming, Embedder embedder, CancellationToken ct)
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
            ids.UnionWith(sameTopic.Where(m => m.Type.IsKnowledge()).Take(MaxTopicNeighbours).Select(m => m.Id));
        }

        var similar = await store.SearchSimilarAsync(incoming.ProjectId, query, SimilaritySearchLimit, ct).ConfigureAwait(false);
        ids.UnionWith(similar
            .Where(s => s.Score >= NeighbourMinCosine)
            .OrderByDescending(s => s.Score)
            .Take(MaxSimilarNeighbours)
            .Select(s => s.MemoryId));

        if (ids.Count == 0)
        {
            return [];
        }

        var memories = (await store.GetByIdsAsync(ids, ct).ConfigureAwait(false))
            .Where(m => m.IsLive && m.ProjectId == incoming.ProjectId && m.Type.IsKnowledge())
            .ToList();
        if (memories.Count == 0)
        {
            return [];
        }

        var memoryIds = memories.Select(m => m.Id).ToList();
        var (vectors, _) = await MemoryVectors.CurrentAsync(
            store, memories, query.ModelKey, query.Vector.Length, embedder.Generator, embedder.Options, ct).ConfigureAwait(false);
        var entityRows = (await store.ListEntitiesAsync(memoryIds, ct).ConfigureAwait(false)).ToLookup(r => r.MemoryId);

        return [.. memories
            .Select(m =>
            {
                var rows = entityRows[m.Id].ToList();
                var strong = EntityExtractor.StrongKeys(rows.Count > 0 ? rows.Select(r => r.Key) : EntityExtractor.Extract(m).Select(e => e.Key));
                var cosine = vectors.TryGetValue(m.Id, out var vector) ? CosineSimilarity.Compute(query.Vector, vector) : 0.0;
                var topicMatch = incoming.TopicKey is not null && incoming.TopicKey == m.TopicKey ? 1.0 : 0.0;
                var score = 0.5 * cosine + 0.3 * TextTokens.Jaccard(candidateStrong, strong) + 0.2 * topicMatch;
                return new Neighbour(m, cosine, strong, score);
            })
            .OrderByDescending(n => n.Score)
            .Take(MaxNeighbours)];
    }

    /// <summary>Reuses an existing topic so the same subject keeps one TopicKey: the neighbour with an
    /// equal key lends its display form; otherwise a neighbour sharing two strong entities lends both.</summary>
    private static Incoming AdoptTopic(Incoming incoming, IReadOnlyList<Neighbour> neighbours)
    {
        if (incoming.TopicKey is { } key && neighbours.FirstOrDefault(n => n.Memory.TopicKey == key) is { } sameKey)
        {
            return incoming with { Topic = sameKey.Memory.Topic ?? incoming.Topic };
        }

        var candidateStrong = EntityExtractor.StrongKeys(incoming.Entities.Select(e => e.Key));
        var sharing = neighbours.FirstOrDefault(n => n.Memory.TopicKey is not null && n.StrongKeys.Count(candidateStrong.Contains) >= 2);
        return sharing is null ? incoming : incoming with { Topic = sharing.Memory.Topic, TopicKey = sharing.Memory.TopicKey };
    }

    private async Task<(MemoryRelation Relation, Neighbour? Target, string? Reason)> JudgeAsync(
        Incoming incoming, IReadOnlyList<Neighbour> neighbours, CancellationToken ct)
    {
        var candidate = RelationSubject.From(incoming.Candidate);
        var subjects = neighbours.Select(n => RelationSubject.From(n.Memory)).ToList();
        var negation = subjects.Select(s => MemoryRelationJudge.HasNegationAsymmetry(candidate, s)).ToList();

        for (var i = 0; i < neighbours.Count; i++)
        {
            if (neighbours[i].Cosine >= FastDuplicateCosine && neighbours[i].Memory.Type == incoming.Candidate.Type && !negation[i])
            {
                return (MemoryRelation.Duplicate, neighbours[i], "near-identical embedding");
            }
        }

        var verdicts = await JudgeWithModelAsync(incoming, candidate, subjects, ct).ConfigureAwait(false)
            ?? JudgeOffline(incoming, neighbours, negation);

        foreach (var relation in VerdictPriority)
        {
            for (var i = 0; i < neighbours.Count; i++)
            {
                if (verdicts[i].Relation == relation)
                {
                    return (relation, neighbours[i], verdicts[i].Reason);
                }
            }
        }

        return (MemoryRelation.Unrelated, null, null);
    }

    /// <summary>The model's verdicts, or null when no chat model is configured or the call failed.</summary>
    private async Task<IReadOnlyList<RelationVerdict>?> JudgeWithModelAsync(
        Incoming incoming, RelationSubject candidate, IReadOnlyList<RelationSubject> subjects, CancellationToken ct)
    {
        var chatClient = await providerResolver.GetChatClientAsync(incoming.ProjectId, ct).ConfigureAwait(false);
        if (chatClient.IsNoOp())
        {
            return null;
        }

        List<ChatMessage> messages =
        [
            new(ChatRole.System, MemoryRelationJudge.SystemPrompt),
            new(ChatRole.User, MemoryRelationJudge.BuildUserPrompt(candidate, subjects)),
        ];

        try
        {
            var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            await usageRecorder.RecordChatUsageAsync(incoming.ProjectId, incoming.SessionId, "MemoryRelationJudge", chatClient, response.Usage, logger, ct).ConfigureAwait(false);
            return MemoryRelationJudge.Parse(response.Text, subjects.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "LLM provider {Provider} failed to judge memory relations for project {ProjectId}; using the offline heuristic.",
                chatClient.GetProviderName(), incoming.ProjectId);
            return null;
        }
    }

    private static IReadOnlyList<RelationVerdict> JudgeOffline(Incoming incoming, IReadOnlyList<Neighbour> neighbours, IReadOnlyList<bool> negation)
    {
        var candidateStrong = EntityExtractor.StrongKeys(incoming.Entities.Select(e => e.Key));
        return [.. neighbours.Select((n, i) => new RelationVerdict(
            MemoryRelationJudge.JudgeOffline(new RelationSignals(
                n.Cosine,
                n.StrongKeys.Overlaps(candidateStrong),
                n.Memory.Type == incoming.Candidate.Type,
                incoming.TopicKey is not null && incoming.TopicKey == n.Memory.TopicKey,
                negation[i])),
            "offline heuristic"))];
    }

    private async Task<ConsolidationResult> ReinforceAsync(Incoming incoming, DomainMemory target, string? reason, CancellationToken ct)
    {
        var evidence = await store.ListEvidenceAsync([target.Id], ct).ConfigureAwait(false);
        var entityRows = await store.ListEntitiesAsync([target.Id], ct).ConfigureAwait(false);
        List<MemoryEvidence> added = [NewEvidence(incoming, target.Id, EvidenceRelation.Reinforced, note: reason)];

        Absorb(target, incoming.Candidate, FactMerger.Merge(EntityExtractor.ParseList(target.FactsJson), incoming.Candidate.Facts));
        if (target.TopicKey is null && incoming.TopicKey is not null)
        {
            target.Topic = incoming.Topic;
            target.TopicKey = incoming.TopicKey;
        }

        target.ReinforcementCount++;
        target.LastReinforcedAt = incoming.Now;

        if (incoming.Candidate.Source == EvidenceSource.Explicit && target.Status == MemoryStatus.Contested)
        {
            foreach (var partner in await MemoryContests.PartnersAsync(store, target.Id, ct).ConfigureAwait(false))
            {
                await SupersedeAndReleaseAsync(partner, target.Id, incoming, "explicit remember resolved the contest", ct).ConfigureAwait(false);
                added.Add(NewEvidence(incoming, target.Id, EvidenceRelation.Superseded, partner.Id, "explicit remember resolved the contest"));
            }

            target.Status = MemoryStatus.Active;
        }

        AddCodeEvidence(added, evidence, incoming, target.Id, incoming.CodeCheck);
        ConfidenceModel.Recompute(target, [.. evidence, .. added]);
        await store.UpdateAsync(target, null, MemoryFields.UnionEntities(target, entityRows, incoming.Entities), added, ct).ConfigureAwait(false);
        return new ConsolidationResult(target, ConsolidationOutcome.Reinforced);
    }

    /// <summary>The merged body and its embedding are produced outside the write transaction from a
    /// snapshot of the target; if the target's title, body or topic changed meanwhile (or it is no longer
    /// live), the candidate becomes a sibling under the same topic instead.</summary>
    private async Task<ConsolidationResult> MergeAsync(Incoming incoming, Guid targetId, string? reason, Embedder embedder, CancellationToken ct)
    {
        if (await ReloadLiveAsync(targetId, ct).ConfigureAwait(false) is not { } snapshot
            || incoming.TopicKey is null
            || incoming.TopicKey != snapshot.TopicKey)
        {
            return await InsertNewAsync(incoming, ct).ConfigureAwait(false);
        }

        var facts = FactMerger.Merge(EntityExtractor.ParseList(snapshot.FactsJson), incoming.Candidate.Facts);
        if (facts.Count > MaxMergedFacts || snapshot.Body.Length + 2 + incoming.Candidate.Body.Length > MaxMergedBodyLength)
        {
            // Too big to grow further: a sibling under the same topic keeps the subject together.
            return await InsertSiblingAsync(incoming, snapshot, ct).ConfigureAwait(false);
        }

        var body = await MergeBodiesAsync(incoming, snapshot, facts, ct).ConfigureAwait(false);
        var reembedding = await embedder.Generator.GenerateAsync(MemoryEmbeddingText.For(snapshot.Title, body), embedder.Options, ct).ConfigureAwait(false);
        var vector = new MemoryVector(incoming.Embedding!.ModelKey, reembedding.Vector.ToArray());

        return await store.InTransactionAsync(async () =>
        {
            var target = await ReloadLiveAsync(targetId, ct).ConfigureAwait(false);
            if (target is null
                || target.Title != snapshot.Title
                || target.Body != snapshot.Body
                || target.TopicKey != snapshot.TopicKey)
            {
                return await InsertSiblingAsync(incoming, snapshot, ct).ConfigureAwait(false);
            }

            var mergedFacts = FactMerger.Merge(EntityExtractor.ParseList(target.FactsJson), incoming.Candidate.Facts);
            if (mergedFacts.Count > MaxMergedFacts)
            {
                return await InsertSiblingAsync(incoming, snapshot, ct).ConfigureAwait(false);
            }

            var evidence = await store.ListEvidenceAsync([target.Id], ct).ConfigureAwait(false);
            var entityRows = await store.ListEntitiesAsync([target.Id], ct).ConfigureAwait(false);
            List<MemoryEvidence> added = [NewEvidence(incoming, target.Id, EvidenceRelation.Merged, note: reason)];

            target.Body = body;
            Absorb(target, incoming.Candidate, mergedFacts);
            target.ReinforcementCount++;
            target.LastReinforcedAt = incoming.Now;
            AddCodeEvidence(added, evidence, incoming, target.Id, incoming.CodeCheck);
            ConfidenceModel.Recompute(target, [.. evidence, .. added]);

            await store.UpdateAsync(target, vector, MemoryFields.UnionEntities(target, entityRows, incoming.Entities), added, ct).ConfigureAwait(false);
            return new ConsolidationResult(target, ConsolidationOutcome.Merged);
        }, ct).ConfigureAwait(false);
    }

    private async Task<string> MergeBodiesAsync(Incoming incoming, DomainMemory target, IReadOnlyList<string> facts, CancellationToken ct)
    {
        var fallback = MemoryMergePrompt.Fallback(target.Body, incoming.Candidate.Body, MaxMergedBodyLength);
        var chatClient = await providerResolver.GetChatClientAsync(incoming.ProjectId, ct).ConfigureAwait(false);
        if (chatClient.IsNoOp())
        {
            return fallback;
        }

        List<ChatMessage> messages =
        [
            new(ChatRole.System, MemoryMergePrompt.SystemPrompt),
            new(ChatRole.User, MemoryMergePrompt.BuildUserPrompt(
                RelationSubject.From(target), RelationSubject.From(incoming.Candidate), facts, MaxMergedBodyLength)),
        ];

        try
        {
            var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            await usageRecorder.RecordChatUsageAsync(incoming.ProjectId, incoming.SessionId, "MemoryMerge", chatClient, response.Usage, logger, ct).ConfigureAwait(false);
            return MemoryMergePrompt.Clean(response.Text, MaxMergedBodyLength) ?? fallback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "LLM provider {Provider} failed to merge memory {MemoryId}; concatenating bodies.", chatClient.GetProviderName(), target.Id);
            return fallback;
        }
    }

    /// <summary>A claim replaces the target only when it was observed after the target's newest support,
    /// is more reliable than everything behind the target, and the code does not refute it; otherwise both
    /// sides are kept as a contest. Equal reliability is enough for an explicit assertion, or for a claim
    /// the code verifies while the target no longer checks out as verified.</summary>
    private async Task<ConsolidationResult> TrySupersedeAsync(
        Incoming incoming, DomainMemory target, CodeCheck targetCheck, string? reason, CancellationToken ct)
    {
        var targetEvidence = await store.ListEvidenceAsync([target.Id], ct).ConfigureAwait(false);
        if (MaySupersede(incoming, target, targetEvidence, targetCheck))
        {
            return await ReplaceAsync(incoming, target, reason, ct).ConfigureAwait(false);
        }

        logger?.LogInformation("Memory {MemoryId} not superseded: the newer claim failed the recency, reliability or code guard.", target.Id);
        return await ContradictAsync(incoming, target, targetCheck, reason, ct).ConfigureAwait(false);
    }

    private static bool MaySupersede(
        Incoming incoming, DomainMemory target, IReadOnlyCollection<MemoryEvidence> targetEvidence, CodeCheck targetCheck)
    {
        if (incoming.CodeCheck.Result == CodeCheckResult.Refuted
            || incoming.ObservedAt <= (ConfidenceModel.LatestSupportAt(targetEvidence) ?? target.CreatedAt))
        {
            return false;
        }

        var candidate = ConfidenceModel.Reliability(incoming.Candidate.Source);
        var existing = ConfidenceModel.BestSourceReliability(targetEvidence);
        var targetVerified = targetCheck.Result switch
        {
            CodeCheckResult.Verified => true,
            CodeCheckResult.Refuted => false,
            _ => ConfidenceModel.LatestCodeCheck(targetEvidence) == EvidenceRelation.Verified,
        };
        return candidate > existing
            || (candidate == existing
                && (incoming.Candidate.Source == EvidenceSource.Explicit
                    || (incoming.CodeCheck.Result == CodeCheckResult.Verified && !targetVerified)));
    }

    private async Task<ConsolidationResult> ContradictAsync(
        Incoming incoming, DomainMemory target, CodeCheck targetCheck, string? reason, CancellationToken ct)
    {
        var candidateCheck = incoming.CodeCheck;
        var targetEvidence = await store.ListEvidenceAsync([target.Id], ct).ConfigureAwait(false);

        if (candidateCheck.Result == CodeCheckResult.Verified && targetCheck.Result == CodeCheckResult.Refuted)
        {
            var refuted = new List<MemoryEvidence>();
            AddCodeEvidence(refuted, targetEvidence, incoming, target.Id, targetCheck);
            await store.AddEvidenceAsync(refuted, ct).ConfigureAwait(false);
            return await ReplaceAsync(incoming, target, "code confirms the new memory and refutes the old one", ct).ConfigureAwait(false);
        }

        if (candidateCheck.Result == CodeCheckResult.Refuted && targetCheck.Result == CodeCheckResult.Verified)
        {
            return await KeepTargetAsync(incoming, target, targetEvidence, targetCheck, ct).ConfigureAwait(false);
        }

        if (incoming.Candidate.Source == EvidenceSource.Explicit && target.Status == MemoryStatus.Contested)
        {
            return await ReplaceAsync(incoming, target, "explicit remember resolved the contest", ct).ConfigureAwait(false);
        }

        var memory = NewMemory(incoming, MemoryStatus.Contested);
        List<MemoryEvidence> evidence =
        [
            NewEvidence(incoming, memory.Id, EvidenceRelation.Created),
            NewEvidence(incoming, memory.Id, EvidenceRelation.Contradicted, target.Id, reason),
        ];
        AddCodeEvidence(evidence, [], incoming, memory.Id, candidateCheck);
        ConfidenceModel.Recompute(memory, evidence);
        await store.InsertAsync(memory, incoming.Embedding, MemoryFields.EntityRows(memory, incoming.Entities), evidence, ct).ConfigureAwait(false);

        List<MemoryEvidence> targetAdded = [NewEvidence(incoming, target.Id, EvidenceRelation.Contradicted, memory.Id, reason)];
        AddCodeEvidence(targetAdded, targetEvidence, incoming, target.Id, targetCheck);
        target.Status = MemoryStatus.Contested;
        ConfidenceModel.Recompute(target, [.. targetEvidence, .. targetAdded]);
        await store.UpdateAsync(target, null, null, targetAdded, ct).ConfigureAwait(false);

        return new ConsolidationResult(memory, ConsolidationOutcome.Contested);
    }

    /// <summary>The code refutes the candidate and confirms the target: the candidate is kept only as
    /// history, already superseded by the target.</summary>
    private async Task<ConsolidationResult> KeepTargetAsync(
        Incoming incoming, DomainMemory target, IReadOnlyList<MemoryEvidence> targetEvidence, CodeCheck targetCheck, CancellationToken ct)
    {
        const string note = "code refutes the new memory and confirms the existing one";
        var loser = NewMemory(incoming, MemoryStatus.Superseded);
        loser.SupersededById = target.Id;
        loser.SupersededAt = incoming.Now;
        List<MemoryEvidence> loserEvidence =
        [
            NewEvidence(incoming, loser.Id, EvidenceRelation.Created),
            NewEvidence(incoming, loser.Id, EvidenceRelation.Superseded, target.Id, note),
        ];
        AddCodeEvidence(loserEvidence, [], incoming, loser.Id, incoming.CodeCheck);
        ConfidenceModel.Recompute(loser, loserEvidence);
        await store.InsertAsync(loser, incoming.Embedding, MemoryFields.EntityRows(loser, incoming.Entities), loserEvidence, ct).ConfigureAwait(false);

        List<MemoryEvidence> targetAdded = [NewEvidence(incoming, target.Id, EvidenceRelation.Contradicted, loser.Id, note)];
        AddCodeEvidence(targetAdded, targetEvidence, incoming, target.Id, targetCheck);
        ConfidenceModel.Recompute(target, [.. targetEvidence, .. targetAdded]);
        await store.UpdateAsync(target, null, null, targetAdded, ct).ConfigureAwait(false);

        return new ConsolidationResult(target, ConsolidationOutcome.Superseded);
    }

    private async Task<ConsolidationResult> ReplaceAsync(Incoming incoming, DomainMemory loser, string? note, CancellationToken ct)
    {
        var memory = NewMemory(incoming, MemoryStatus.Active);
        List<MemoryEvidence> evidence =
        [
            NewEvidence(incoming, memory.Id, EvidenceRelation.Created),
            NewEvidence(incoming, memory.Id, EvidenceRelation.Superseded, loser.Id, note),
        ];
        AddCodeEvidence(evidence, [], incoming, memory.Id, incoming.CodeCheck);
        ConfidenceModel.Recompute(memory, evidence);
        await store.InsertAsync(memory, incoming.Embedding, MemoryFields.EntityRows(memory, incoming.Entities), evidence, ct).ConfigureAwait(false);
        await SupersedeAndReleaseAsync(loser, memory.Id, incoming, note, ct).ConfigureAwait(false);
        return new ConsolidationResult(memory, ConsolidationOutcome.Superseded);
    }

    private Task<ConsolidationResult> InsertNewAsync(Incoming incoming, CancellationToken ct) =>
        store.InTransactionAsync(async () =>
        {
            if (await FindConcurrentDuplicateAsync(incoming, ct).ConfigureAwait(false) is { } duplicate)
            {
                return await ReinforceAsync(incoming, duplicate, ConcurrentDuplicateNote, ct).ConfigureAwait(false);
            }

            var memory = NewMemory(incoming, MemoryStatus.Active);
            List<MemoryEvidence> evidence = [NewEvidence(incoming, memory.Id, EvidenceRelation.Created)];
            AddCodeEvidence(evidence, [], incoming, memory.Id, incoming.CodeCheck);
            ConfidenceModel.Recompute(memory, evidence);
            await store.InsertAsync(memory, incoming.Embedding, MemoryFields.EntityRows(memory, incoming.Entities), evidence, ct).ConfigureAwait(false);
            return new ConsolidationResult(memory, ConsolidationOutcome.Created);
        }, ct);

    /// <summary>The same claim stored by another consolidation after this one searched for neighbours.
    /// Read inside the write transaction, so two concurrent writers of one claim cannot both insert it.</summary>
    private async Task<DomainMemory?> FindConcurrentDuplicateAsync(Incoming incoming, CancellationToken ct)
    {
        var recent = (await store.ListLiveKnowledgeCreatedSinceAsync(incoming.ProjectId, incoming.Now - ConcurrentWindow, ct).ConfigureAwait(false))
            .Where(m => m.Type == incoming.Candidate.Type)
            .ToList();
        if (recent.Count == 0)
        {
            return null;
        }

        var vectors = await store.GetEmbeddingsAsync([.. recent.Select(m => m.Id)], ct).ConfigureAwait(false);
        var candidate = RelationSubject.From(incoming.Candidate);
        var title = TextTokens.Normalize(incoming.Candidate.Title);
        var body = TextTokens.Normalize(incoming.Candidate.Body);

        bool SameEmbedding(DomainMemory m) =>
            incoming.Embedding is { } query
            && vectors.TryGetValue(m.Id, out var stored)
            && stored.ModelKey == query.ModelKey
            && stored.Vector.Length == query.Vector.Length
            && CosineSimilarity.Compute(query.Vector, stored.Vector) >= FastDuplicateCosine;

        return recent.FirstOrDefault(m =>
            !MemoryRelationJudge.HasNegationAsymmetry(candidate, RelationSubject.From(m))
            && (SameEmbedding(m) || (TextTokens.Normalize(m.Title) == title && TextTokens.Normalize(m.Body) == body)));
    }

    private Task<ConsolidationResult> InsertSiblingAsync(Incoming incoming, DomainMemory target, CancellationToken ct) =>
        InsertNewAsync(incoming with { Topic = target.Topic, TopicKey = target.TopicKey }, ct);

    private Task SupersedeAndReleaseAsync(DomainMemory loser, Guid winnerId, Incoming incoming, string? note, CancellationToken ct) =>
        MemoryContests.SupersedeAndReleaseAsync(
            store, loser, winnerId, incoming.Now, NewEvidence(incoming, loser.Id, EvidenceRelation.Superseded, winnerId, note), ct);

    private async Task<DomainMemory?> ReloadLiveAsync(Guid id, CancellationToken ct)
    {
        var memory = (await store.GetByIdsAsync([id], ct).ConfigureAwait(false)).FirstOrDefault();
        return memory is { IsLive: true } ? memory : null;
    }

    private static void AddCodeEvidence(
        List<MemoryEvidence> added, IReadOnlyCollection<MemoryEvidence> existing, Incoming incoming, Guid memoryId, CodeCheck check)
    {
        if (MemoryCodeChecker.Evidence(incoming.ProjectId, memoryId, check, [.. existing, .. added], incoming.Now) is { } row)
        {
            added.Add(row);
        }
    }

    /// <summary>Folds the candidate's facts, concepts and files into <paramref name="memory"/>.</summary>
    private static void Absorb(DomainMemory memory, MemoryCandidate candidate, IReadOnlyList<string> facts)
    {
        memory.FactsJson = JsonSerializer.Serialize(facts);
        memory.ConceptsJson = MemoryFields.UnionJson(memory.ConceptsJson, candidate.Concepts);
        memory.FilesReadJson = MemoryFields.UnionJson(memory.FilesReadJson, candidate.FilesRead);
        memory.FilesModifiedJson = MemoryFields.UnionJson(memory.FilesModifiedJson, candidate.FilesModified);
        memory.ScopePath ??= candidate.ScopePath;
    }

    private static MemoryEvidence NewEvidence(
        Incoming incoming, Guid memoryId, EvidenceRelation relation, Guid? sourceMemoryId = null, string? note = null) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = incoming.ProjectId,
        MemoryId = memoryId,
        Relation = relation,
        Source = incoming.Candidate.Source,
        SessionId = incoming.SessionId,
        SourceMemoryId = sourceMemoryId,
        ObservedAt = incoming.ObservedAt,
        Note = MemoryFields.Note(note),
    };

    private static DomainMemory NewMemory(Incoming incoming, MemoryStatus status)
    {
        var candidate = incoming.Candidate;
        return new DomainMemory
        {
            Id = Guid.NewGuid(),
            ProjectId = incoming.ProjectId,
            Type = candidate.Type,
            Status = status,
            Title = candidate.Title,
            Body = candidate.Body,
            Topic = incoming.Topic,
            TopicKey = incoming.TopicKey,
            ScopePath = candidate.ScopePath,
            FactsJson = JsonSerializer.Serialize(candidate.Facts),
            ConceptsJson = MemoryFields.ToJsonOrNull(candidate.Concepts),
            FilesReadJson = MemoryFields.ToJsonOrNull(candidate.FilesRead),
            FilesModifiedJson = MemoryFields.ToJsonOrNull(candidate.FilesModified),
            Tier = MemoryTier.Working,
            CreatedFromSessionId = incoming.SessionId,
            CreatedAt = incoming.Now,
            LastReinforcedAt = incoming.Now,
        };
    }

    /// <summary><paramref name="Now"/> stamps the write; <paramref name="ObservedAt"/> is when the claim
    /// itself was made and orders it against the evidence behind existing memories.</summary>
    private sealed record Incoming(
        Guid ProjectId,
        Guid? SessionId,
        MemoryCandidate Candidate,
        IReadOnlyList<ExtractedEntity> Entities,
        string? Topic,
        string? TopicKey,
        DateTimeOffset Now,
        DateTimeOffset ObservedAt)
    {
        public MemoryVector? Embedding { get; init; }
        public CodeCheck CodeCheck { get; init; } = CodeCheck.Unknown;
    }

    private sealed record Neighbour(DomainMemory Memory, double Cosine, HashSet<string> StrongKeys, double Score);

    private sealed record Embedder(IEmbeddingGenerator<string, Embedding<float>> Generator, EmbeddingGenerationOptions? Options);
}
