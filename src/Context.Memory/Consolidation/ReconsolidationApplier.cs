using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Memory.Consolidation;

/// <summary>
/// Persists approved reconsolidation groups and change-log retirements transactionally, re-checking
/// that every source memory remained unmodified since planning.
/// </summary>
internal sealed class ReconsolidationApplier(IMemoryStore store, IProviderResolver providerResolver)
{
    private const string MergedNote = "reconsolidated into a canonical memory";

    public async Task<ReconsolidationReport> ApplyAsync(
        Guid projectId, ReconsolidationReport report, ReconsolidationApplyOptions options, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var applied = new List<int>();
        var excluded = new List<int>();
        var skipped = new List<SkippedGroup>();
        IEmbeddingGenerator<string, Embedding<float>>? generator = null;

        foreach (var group in report.Groups)
        {
            if (options.ExcludeGroups.Contains(group.Index))
            {
                excluded.Add(group.Index);
                continue;
            }

            if (group.Ambiguous)
            {
                skipped.Add(new SkippedGroup(group.Index, "ambiguous"));
                continue;
            }

            if (await FindChangedSourceAsync(projectId, group, ct).ConfigureAwait(false) is { } stale)
            {
                skipped.Add(new SkippedGroup(group.Index, ChangedReason(stale)));
                continue;
            }

            // Embedding calls stay outside the write transaction.
            var vectors = new List<MemoryVector>();
            if (group.Canonicals.Count > 0)
            {
                generator ??= await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
                var modelKey = generator.GetProviderModelKey();
                var embeddings = await generator.GenerateAsync(
                    group.Canonicals.Select(c => MemoryEmbeddingText.For(c.Title, c.Body)), cancellationToken: ct).ConfigureAwait(false);
                vectors.AddRange(embeddings.Select(e => new MemoryVector(modelKey, e.Vector.ToArray())));
            }

            var skipReason = await ApplyGroupTransactionAsync(projectId, group, vectors, options.ResolveContradictions, now, ct).ConfigureAwait(false);
            if (skipReason is null)
            {
                applied.Add(group.Index);
            }
            else
            {
                skipped.Add(new SkippedGroup(group.Index, skipReason));
            }
        }

        var (retiredCount, skippedChangeLogs) = await RetireChangeLogsAsync(projectId, report.RetiredChangeLogs, ct).ConfigureAwait(false);
        return report with
        {
            Outcome = new ReconsolidationApplyOutcome(now, applied, excluded, skipped, retiredCount, skippedChangeLogs),
        };
    }

    private async Task<string?> ApplyGroupTransactionAsync(
        Guid projectId,
        ReconsolidationGroup group,
        List<MemoryVector> vectors,
        bool resolveContradictions,
        DateTimeOffset now,
        CancellationToken ct)
    {
        return await store.InTransactionAsync<string?>(async () =>
        {
            if (await FindChangedSourceAsync(projectId, group, ct).ConfigureAwait(false) is { } changed)
            {
                return ChangedReason(changed);
            }

            var current = (await store.GetByIdsAsync(TouchedIds(group), ct).ConfigureAwait(false)).ToDictionary(m => m.Id);
            for (var i = 0; i < group.Canonicals.Count; i++)
            {
                await ApplyCanonicalAsync(projectId, group.Canonicals[i], current, vectors[i], now, ct).ConfigureAwait(false);
            }

            foreach (var contradiction in group.Contradictions)
            {
                await ApplyContradictionAsync(projectId, contradiction, resolveContradictions, now, ct).ConfigureAwait(false);
            }

            return null;
        }, ct).ConfigureAwait(false);
    }

    private async Task<(int RetiredCount, List<Guid> Skipped)> RetireChangeLogsAsync(
        Guid projectId, IReadOnlyList<RetiredChangeLog> plannedChangeLogs, CancellationToken ct)
    {
        var retiredCount = 0;
        var skippedChangeLogs = new List<Guid>();
        foreach (var planned in plannedChangeLogs)
        {
            var retired = await store.InTransactionAsync(async () =>
            {
                var memory = (await store.GetByIdsAsync([planned.Id], ct).ConfigureAwait(false)).FirstOrDefault();
                return memory is not null
                    && IsUnchanged(memory, projectId, planned.Status, planned.LastReinforcedAt)
                    && await MemoryContests.RetireAsync(store, memory.Id, ct).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            if (retired)
            {
                retiredCount++;
            }
            else
            {
                skippedChangeLogs.Add(planned.Id);
            }
        }

        return (retiredCount, skippedChangeLogs);
    }

    private async Task<ReconsolidationSource?> FindChangedSourceAsync(Guid projectId, ReconsolidationGroup group, CancellationToken ct)
    {
        var touchedIds = TouchedIds(group);
        var current = (await store.GetByIdsAsync(touchedIds, ct).ConfigureAwait(false)).ToDictionary(m => m.Id);
        return group.Sources
            .Where(s => touchedIds.Contains(s.Id))
            .FirstOrDefault(s => !current.TryGetValue(s.Id, out var m) || !IsUnchanged(m, projectId, s.Status, s.LastReinforcedAt));
    }

    private static HashSet<Guid> TouchedIds(ReconsolidationGroup group) =>
        group.Canonicals.SelectMany(c => c.SourceIds)
            .Concat(group.Contradictions.SelectMany(c => new[] { c.A, c.B }))
            .ToHashSet();

    private static string ChangedReason(ReconsolidationSource source) => $"source {source.Id} changed since the plan";

    private static bool IsUnchanged(DomainMemory memory, Guid projectId, MemoryStatus status, DateTimeOffset lastReinforcedAt) =>
        memory.IsLive
        && memory.ProjectId == projectId
        && memory.Status == status
        && memory.LastReinforcedAt == lastReinforcedAt;

    /// <summary>
    /// Inserts the canonical with copies of its sources' evidence plus one Merged link per source, then
    /// supersedes the sources. A contest a source was part of carries over to the canonical.
    /// </summary>
    private async Task ApplyCanonicalAsync(
        Guid projectId,
        ReconsolidationCanonical canonical,
        IReadOnlyDictionary<Guid, DomainMemory> current,
        MemoryVector embedding,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var sources = canonical.SourceIds.Select(id => current[id]).ToList();
        var sourceEvidence = (await store.ListEvidenceAsync(canonical.SourceIds.ToList(), ct).ConfigureAwait(false)).ToLookup(e => e.MemoryId);
        var sourceEntities = await store.ListEntitiesAsync(canonical.SourceIds.ToList(), ct).ConfigureAwait(false);
        var concepts = sources.Aggregate(new List<string>(), (acc, s) => MemoryFields.UnionList(acc, EntityExtractor.ParseList(s.ConceptsJson)));

        var memory = BuildCanonicalMemory(projectId, canonical, sources, concepts, now);
        var evidence = BuildCanonicalEvidence(projectId, memory.Id, sources, sourceEvidence, now);

        var sourceIds = canonical.SourceIds.ToHashSet();
        var outsidePartners = new List<DomainMemory>();
        foreach (var source in sources.Where(s => s.Status == MemoryStatus.Contested))
        {
            outsidePartners.AddRange((await MemoryContests.PartnersAsync(store, source.Id, ct).ConfigureAwait(false)).Where(p => !sourceIds.Contains(p.Id)));
        }

        if (outsidePartners.Count > 0)
        {
            memory.Status = MemoryStatus.Contested;
        }

        ConfidenceModel.Recompute(memory, evidence);

        var candidate = new MemoryCandidate(
            canonical.Type, canonical.Title, canonical.Body, EvidenceSource.Consolidation, canonical.Facts, concepts,
            canonical.FilesRead, canonical.FilesModified, canonical.Topic) { Entities = canonical.Entities };
        await store.InsertAsync(
            memory,
            embedding,
            MemoryFields.UnionEntities(memory, sourceEntities, EntityExtractor.Extract(candidate)),
            evidence,
            ct).ConfigureAwait(false);

        if (outsidePartners.Count > 0)
        {
            await store.AddEvidenceAsync(
                [.. outsidePartners.DistinctBy(p => p.Id).Select(p => NewEvidence(projectId, p.Id, EvidenceRelation.Contradicted, memory.Id, now, "contest carried over to the canonical memory"))],
                ct).ConfigureAwait(false);
        }

        var superseded = new List<MemoryEvidence>();
        foreach (var source in sources)
        {
            if (await store.SupersedeAsync(source.Id, memory.Id, now, ct).ConfigureAwait(false))
            {
                superseded.Add(NewEvidence(projectId, source.Id, EvidenceRelation.Superseded, memory.Id, now, MergedNote));
            }
        }

        await store.AddEvidenceAsync(superseded, ct).ConfigureAwait(false);
    }

    private static DomainMemory BuildCanonicalMemory(
        Guid projectId,
        ReconsolidationCanonical canonical,
        List<DomainMemory> sources,
        List<string> concepts,
        DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Type = canonical.Type,
        Status = MemoryStatus.Active,
        Title = canonical.Title,
        Body = canonical.Body,
        Topic = canonical.Topic,
        TopicKey = TopicNormalizer.Normalize(canonical.Topic),
        ScopePath = ScopePathHeuristics.DeriveFromFiles(canonical.FilesModified, canonical.FilesRead)
            ?? sources.Select(s => s.ScopePath).FirstOrDefault(p => p is not null),
        FactsJson = JsonSerializer.Serialize(canonical.Facts),
        ConceptsJson = MemoryFields.ToJsonOrNull(concepts),
        FilesReadJson = MemoryFields.ToJsonOrNull(canonical.FilesRead),
        FilesModifiedJson = MemoryFields.ToJsonOrNull(canonical.FilesModified),
        Tier = sources.Max(s => s.Tier),
        Pinned = sources.Any(s => s.Pinned),
        ReinforcementCount = sources.Sum(s => s.ReinforcementCount),
        CreatedAt = now,
        LastReinforcedAt = sources.Max(s => s.LastReinforcedAt),
    };

    private static List<MemoryEvidence> BuildCanonicalEvidence(
        Guid projectId,
        Guid canonicalId,
        List<DomainMemory> sources,
        ILookup<Guid, MemoryEvidence> sourceEvidence,
        DateTimeOffset now)
    {
        var evidence = new List<MemoryEvidence>();
        foreach (var source in sources)
        {
            var rows = sourceEvidence[source.Id].ToList();
            evidence.AddRange(rows.Select(e => new MemoryEvidence
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                MemoryId = canonicalId,
                Relation = e.Relation,
                Source = e.Source,
                SessionId = e.SessionId,
                SourceMemoryId = e.SourceMemoryId ?? source.Id,
                ObservedAt = e.ObservedAt,
                Note = e.Note,
            }));

            // The link shares the source's first session so it counts as the same independent source.
            evidence.Add(NewEvidence(projectId, canonicalId, EvidenceRelation.Merged, source.Id, now, MergedNote,
                rows.Where(e => e.Source is not EvidenceSource.Code and not EvidenceSource.Explicit && e.SessionId is not null)
                    .OrderBy(e => e.ObservedAt)
                    .Select(e => e.SessionId)
                    .FirstOrDefault()));
        }

        return evidence;
    }

    /// <summary>With a code check that confirms one side and refutes the other (and resolution allowed),
    /// the refuted side is superseded; otherwise both sides are marked Contested against each other.</summary>
    private async Task ApplyContradictionAsync(
        Guid projectId, ReconsolidationContradiction contradiction, bool resolve, DateTimeOffset now, CancellationToken ct)
    {
        var pair = (await store.GetByIdsAsync([contradiction.A, contradiction.B], ct).ConfigureAwait(false)).ToDictionary(m => m.Id);
        if (!pair.TryGetValue(contradiction.A, out var a) || !pair.TryGetValue(contradiction.B, out var b) || !a.IsLive || !b.IsLive)
        {
            return;
        }

        var evidence = (await store.ListEvidenceAsync([a.Id, b.Id], ct).ConfigureAwait(false)).ToLookup(e => e.MemoryId);
        var check = contradiction.CodeCheck;
        var checkA = new CodeCheck(check?.A ?? CodeCheckResult.Unknown, check?.Note);
        var checkB = new CodeCheck(check?.B ?? CodeCheckResult.Unknown, check?.Note);

        if (resolve && check?.WinnerId is { } winnerId)
        {
            await ResolveContradictionByWinnerAsync(projectId, contradiction, a, b, checkA, checkB, winnerId, evidence, now, ct).ConfigureAwait(false);
            return;
        }

        await ContestAsync(a, b, checkA).ConfigureAwait(false);
        await ContestAsync(b, a, checkB).ConfigureAwait(false);

        async Task ContestAsync(DomainMemory memory, DomainMemory partner, CodeCheck codeCheck)
        {
            List<MemoryEvidence> added = [NewEvidence(projectId, memory.Id, EvidenceRelation.Contradicted, partner.Id, now, contradiction.Reason)];
            if (MemoryCodeChecker.Evidence(projectId, memory.Id, codeCheck, evidence[memory.Id], now) is { } code)
            {
                added.Add(code);
            }

            memory.Status = MemoryStatus.Contested;
            ConfidenceModel.Recompute(memory, [.. evidence[memory.Id], .. added]);
            await store.UpdateAsync(memory, null, null, added, ct).ConfigureAwait(false);
        }
    }

    private async Task ResolveContradictionByWinnerAsync(
        Guid projectId,
        ReconsolidationContradiction contradiction,
        DomainMemory a,
        DomainMemory b,
        CodeCheck checkA,
        CodeCheck checkB,
        Guid winnerId,
        ILookup<Guid, MemoryEvidence> evidence,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var (winner, loser, winnerCheck, loserCheck) = winnerId == a.Id ? (a, b, checkA, checkB) : (b, a, checkB, checkA);
        var note = $"code check settled the contradiction: {contradiction.Reason}";

        if (MemoryCodeChecker.Evidence(projectId, loser.Id, loserCheck, evidence[loser.Id], now) is { } loserCode)
        {
            await store.AddEvidenceAsync([loserCode], ct).ConfigureAwait(false);
        }

        await MemoryContests.SupersedeAndReleaseAsync(
            store, loser, winner.Id, now, NewEvidence(projectId, loser.Id, EvidenceRelation.Superseded, winner.Id, now, note), ct).ConfigureAwait(false);

        List<MemoryEvidence> added = [NewEvidence(projectId, winner.Id, EvidenceRelation.Superseded, loser.Id, now, note)];
        if (MemoryCodeChecker.Evidence(projectId, winner.Id, winnerCheck, evidence[winner.Id], now) is { } winnerCode)
        {
            added.Add(winnerCode);
        }

        if (winner.Status == MemoryStatus.Contested && (await MemoryContests.PartnersAsync(store, winner.Id, ct).ConfigureAwait(false)).Count == 0)
        {
            winner.Status = MemoryStatus.Active;
        }

        ConfidenceModel.Recompute(winner, [.. evidence[winner.Id], .. added]);
        await store.UpdateAsync(winner, null, null, added, ct).ConfigureAwait(false);
    }

    private static MemoryEvidence NewEvidence(
        Guid projectId, Guid memoryId, EvidenceRelation relation, Guid sourceMemoryId, DateTimeOffset at, string? note, Guid? sessionId = null) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        MemoryId = memoryId,
        Relation = relation,
        Source = EvidenceSource.Consolidation,
        SessionId = sessionId,
        SourceMemoryId = sourceMemoryId,
        ObservedAt = at,
        Note = MemoryFields.Note(note),
    };
}
