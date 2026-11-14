using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using Context.Shared;
using Microsoft.Extensions.Logging;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Memory.Consolidation;

/// <summary>
/// Persists non-merge consolidation outcomes (create, reinforce, supersede, contest, keep-target)
/// inside store transactions while checking for concurrent duplicate insertions.
/// </summary>
internal sealed class ConsolidationOutcomeWriter(IMemoryStore store, ILogger? logger)
{
    private const string ConcurrentDuplicateNote = "same claim stored concurrently";
    private static readonly TimeSpan ConcurrentWindow = TimeSpan.FromHours(1);

    public Task<ConsolidationResult> InsertNewAsync(IncomingMemory incoming, CancellationToken ct) =>
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

    public Task<ConsolidationResult> InsertSiblingAsync(IncomingMemory incoming, DomainMemory target, CancellationToken ct) =>
        InsertNewAsync(incoming with { Topic = target.Topic, TopicKey = target.TopicKey }, ct);

    public Task<ConsolidationResult> InsertAlongsideAsync(IncomingMemory incoming, DomainMemory neighbour, CancellationToken ct) =>
        incoming.TopicKey is not null && incoming.TopicKey == neighbour.TopicKey
            ? InsertSiblingAsync(incoming, neighbour, ct)
            : InsertNewAsync(incoming, ct);

    public Task<ConsolidationResult> ApplyVerdictInTransactionAsync(
        IncomingMemory incoming,
        ConsolidationNeighbour neighbour,
        MemoryRelation relation,
        string? reason,
        CodeCheck targetCheck,
        CancellationToken ct) =>
        store.InTransactionAsync(async () =>
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
        }, ct);

    public async Task<ConsolidationResult> ReinforceAsync(
        IncomingMemory incoming, DomainMemory target, string? reason, CancellationToken ct)
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

    public async Task<DomainMemory?> ReloadLiveAsync(Guid id, CancellationToken ct)
    {
        var memory = (await store.GetByIdsAsync([id], ct).ConfigureAwait(false)).FirstOrDefault();
        return memory is { IsLive: true } ? memory : null;
    }

    public Task SupersedeAndReleaseAsync(DomainMemory loser, Guid winnerId, IncomingMemory incoming, string? note, CancellationToken ct) =>
        MemoryContests.SupersedeAndReleaseAsync(
            store, loser, winnerId, incoming.Now, NewEvidence(incoming, loser.Id, EvidenceRelation.Superseded, winnerId, note), ct);

    public static void AddCodeEvidence(
        List<MemoryEvidence> added, IReadOnlyCollection<MemoryEvidence> existing, IncomingMemory incoming, Guid memoryId, CodeCheck check)
    {
        if (MemoryCodeChecker.Evidence(incoming.ProjectId, memoryId, check, [.. existing, .. added], incoming.Now) is { } row)
        {
            added.Add(row);
        }
    }

    public static void Absorb(DomainMemory memory, MemoryCandidate candidate, IReadOnlyList<string> facts)
    {
        memory.FactsJson = JsonSerializer.Serialize(facts);
        memory.ConceptsJson = MemoryFields.UnionJson(memory.ConceptsJson, candidate.Concepts);
        memory.FilesReadJson = MemoryFields.UnionJson(memory.FilesReadJson, candidate.FilesRead);
        memory.FilesModifiedJson = MemoryFields.UnionJson(memory.FilesModifiedJson, candidate.FilesModified);
        memory.ScopePath ??= candidate.ScopePath;
    }

    public static MemoryEvidence NewEvidence(
        IncomingMemory incoming, Guid memoryId, EvidenceRelation relation, Guid? sourceMemoryId = null, string? note = null) => new()
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

    private async Task<ConsolidationResult> TrySupersedeAsync(
        IncomingMemory incoming, DomainMemory target, CodeCheck targetCheck, string? reason, CancellationToken ct)
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
        IncomingMemory incoming, DomainMemory target, IReadOnlyCollection<MemoryEvidence> targetEvidence, CodeCheck targetCheck)
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
        IncomingMemory incoming, DomainMemory target, CodeCheck targetCheck, string? reason, CancellationToken ct)
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

    private async Task<ConsolidationResult> KeepTargetAsync(
        IncomingMemory incoming, DomainMemory target, IReadOnlyList<MemoryEvidence> targetEvidence, CodeCheck targetCheck, CancellationToken ct)
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

    private async Task<ConsolidationResult> ReplaceAsync(IncomingMemory incoming, DomainMemory loser, string? note, CancellationToken ct)
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

    private async Task<DomainMemory?> FindConcurrentDuplicateAsync(IncomingMemory incoming, CancellationToken ct)
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
            && CosineSimilarity.Compute(query.Vector, stored.Vector) >= MemoryConsolidator.FastDuplicateCosine;

        return recent.FirstOrDefault(m =>
            !MemoryRelationJudge.HasNegationAsymmetry(candidate, RelationSubject.From(m))
            && (SameEmbedding(m) || (TextTokens.Normalize(m.Title) == title && TextTokens.Normalize(m.Body) == body)));
    }

    private static DomainMemory NewMemory(IncomingMemory incoming, MemoryStatus status)
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
}
