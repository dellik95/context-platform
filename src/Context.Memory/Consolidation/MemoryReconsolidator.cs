using System.Text.Json;
using System.Text.RegularExpressions;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using Context.Providers;
using Context.Providers.Abstractions;
using Context.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Memory.Consolidation;

/// <summary>
/// Batch reconsolidation of a project's live knowledge. <see cref="PlanAsync"/> groups related
/// memories (a shared strong entity or topic key, backed by embedding similarity or a second shared
/// entity), asks the chat model to rewrite each group into canonical memories, and validates that no
/// source fact is lost; it writes nothing but refreshed embeddings. It also lists change logs stored as
/// memories for retirement. <see cref="ApplyAsync"/> re-checks every source against the plan, inserts the
/// canonicals, supersedes their sources and retires the change logs.
/// </summary>
public sealed partial class MemoryReconsolidator(
    IMemoryStore store,
    ICodeVerifier codeVerifier,
    IProviderResolver providerResolver,
    IUsageRecorder usageRecorder,
    ILogger<MemoryReconsolidator>? logger = null)
{
    public const double EdgeMinCosine = 0.60;
    public const int EdgeMinSharedStrong = 2;
    public const int MaxGroupSize = 8;
    public const int MaxCanonicalFacts = 40;

    private const string MergedNote = "reconsolidated into a canonical memory";

    private readonly MemoryCodeChecker codeChecker = new(codeVerifier, logger);

    public async Task<ReconsolidationReport> PlanAsync(Guid projectId, CancellationToken ct)
    {
        var live = (await store.ListLiveKnowledgeAsync(projectId, ct).ConfigureAwait(false))
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToList();
        var evidence = (await store.ListEvidenceAsync([.. live.Select(m => m.Id)], ct).ConfigureAwait(false)).ToLookup(e => e.MemoryId);

        var changeLogs = new List<RetiredChangeLog>();
        var knowledge = new List<DomainMemory>();
        foreach (var memory in live)
        {
            if (ChangeLogReason(memory, evidence[memory.Id]) is { } reason)
            {
                changeLogs.Add(new RetiredChangeLog(memory.Id, memory.Title, memory.Status, memory.LastReinforcedAt, reason));
            }
            else
            {
                knowledge.Add(memory);
            }
        }

        var generator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
        var (vectors, reembedded) = await MemoryVectors.CurrentAsync(
            store, knowledge, generator.GetProviderModelKey(), null, generator, null, ct).ConfigureAwait(false);
        var entityRows = (await store.ListEntitiesAsync([.. knowledge.Select(m => m.Id)], ct).ConfigureAwait(false)).ToLookup(r => r.MemoryId);

        var items = knowledge.Select(m =>
        {
            var rows = entityRows[m.Id].ToList();
            IReadOnlyList<ExtractedEntity> entities = rows.Count > 0 ? [.. rows.Select(r => new ExtractedEntity(r.Key, r.Display))] : EntityExtractor.Extract(m);
            return new Item(
                m,
                EntityExtractor.StrongKeys(entities.Select(e => e.Key)),
                m.TopicKey ?? TopicNormalizer.Normalize(m.Topic),
                vectors.GetValueOrDefault(m.Id),
                EntityExtractor.ParseList(m.FactsJson));
        }).ToList();

        var chatClient = await providerResolver.GetChatClientAsync(projectId, ct).ConfigureAwait(false);
        var usedModel = false;
        var groups = new List<ReconsolidationGroup>();

        foreach (var cluster in Cluster(items))
        {
            GroupPlan? plan = null;
            if (!chatClient.IsNoOp())
            {
                plan = await PlanWithModelAsync(projectId, chatClient, cluster, ct).ConfigureAwait(false);
                usedModel |= plan is not null;
            }

            plan ??= PlanOffline(cluster, chatClient.IsNoOp() ? "offline: no chat model, duplicates merged by fact union" : "offline: the chat model failed, duplicates merged by fact union");
            if (plan.Canonicals.Count == 0 && plan.Contradictions.Count == 0 && !plan.Ambiguous)
            {
                continue;
            }

            groups.Add(new ReconsolidationGroup(
                groups.Count + 1,
                [.. cluster.Select(i => new ReconsolidationSource(i.Memory.Id, i.Memory.Title, i.Memory.Type, i.Memory.Status, i.Memory.LastReinforcedAt))],
                plan.Canonicals,
                await CheckContradictionsAsync(plan.Contradictions, ct).ConfigureAwait(false),
                plan.KeepSeparate,
                plan.Ambiguous,
                plan.Note));
        }

        var touched = groups
            .Where(g => !g.Ambiguous)
            .SelectMany(g => g.Canonicals.SelectMany(c => c.SourceIds).Concat(g.Contradictions.SelectMany(c => new[] { c.A, c.B })))
            .ToHashSet();

        return new ReconsolidationReport(
            groups,
            changeLogs,
            live.Count - touched.Count - changeLogs.Count,
            new ReconsolidationStats(
                live.Count,
                reembedded,
                groups.Count,
                groups.Count(g => g.Ambiguous),
                groups.Sum(g => g.Canonicals.Count),
                groups.Sum(g => g.Canonicals.Sum(c => c.SourceIds.Count)),
                groups.Sum(g => g.Contradictions.Count),
                changeLogs.Count,
                usedModel));
    }

    /// <summary>
    /// Applies every non-excluded, non-ambiguous group whose sources are still live and unchanged since
    /// the plan, then retires the planned change logs. Each group and each retirement is re-validated
    /// and written in its own store transaction. Nothing is deleted: sources are superseded and change
    /// logs become Obsolete.
    /// </summary>
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

            var skipReason = await store.InTransactionAsync<string?>(async () =>
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
                    await ApplyContradictionAsync(projectId, contradiction, options.ResolveContradictions, now, ct).ConfigureAwait(false);
                }

                return null;
            }, ct).ConfigureAwait(false);

            if (skipReason is null)
            {
                applied.Add(group.Index);
            }
            else
            {
                skipped.Add(new SkippedGroup(group.Index, skipReason));
            }
        }

        var retiredCount = 0;
        var skippedChangeLogs = new List<Guid>();
        foreach (var planned in report.RetiredChangeLogs)
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

        return report with
        {
            Outcome = new ReconsolidationApplyOutcome(now, applied, excluded, skipped, retiredCount, skippedChangeLogs),
        };
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

    /// <summary>A change summary stored as a memory: explicit, pinned and contested memories are never
    /// touched, and a change-log title stays while one of its facts states more than a change.</summary>
    private static string? ChangeLogReason(DomainMemory memory, IEnumerable<MemoryEvidence> evidence)
    {
        if (memory.Pinned || memory.Status == MemoryStatus.Contested || evidence.Any(e => e.Source == EvidenceSource.Explicit))
        {
            return null;
        }

        if (ChangedFilesTitleRegex().IsMatch(memory.Title))
        {
            return "file-change summary";
        }

        return MemoryCandidateValidator.IsChangeLog(memory.Title, EntityExtractor.ParseList(memory.FactsJson))
            ? "change log without durable facts"
            : null;
    }

    /// <summary>
    /// Connected components of the relatedness graph, in creation order. An edge needs a shared strong
    /// entity or topic key, and either embedding similarity or a second shared strong entity; a
    /// component larger than <see cref="MaxGroupSize"/> is split along its most similar edges.
    /// </summary>
    private static List<List<Item>> Cluster(IReadOnlyList<Item> items)
    {
        var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var keys = items[i].TopicKey is { } topicKey ? items[i].Strong.Append($"topic:{topicKey}") : items[i].Strong;
            foreach (var key in keys)
            {
                if (!postings.TryGetValue(key, out var list))
                {
                    postings[key] = list = [];
                }

                list.Add(i);
            }
        }

        var pairs = new HashSet<(int, int)>();
        foreach (var list in postings.Values)
        {
            for (var a = 0; a < list.Count; a++)
            {
                for (var b = a + 1; b < list.Count; b++)
                {
                    pairs.Add((list[a], list[b]));
                }
            }
        }

        var edges = new List<(int A, int B, double Cosine)>();
        foreach (var (a, b) in pairs)
        {
            var cosine = Cosine(items[a], items[b]);
            if (cosine >= EdgeMinCosine || items[a].Strong.Count(items[b].Strong.Contains) >= EdgeMinSharedStrong)
            {
                edges.Add((a, b, cosine));
            }
        }

        var components = new DisjointSets(items.Count);
        foreach (var (a, b, _) in edges)
        {
            components.Union(a, b);
        }

        var clusters = new List<List<int>>();
        foreach (var component in components.Groups().Where(g => g.Count >= 2))
        {
            if (component.Count <= MaxGroupSize)
            {
                clusters.Add(component);
                continue;
            }

            var members = component.ToHashSet();
            var split = new DisjointSets(items.Count);
            foreach (var (a, b, _) in edges.Where(e => members.Contains(e.A)).OrderByDescending(e => e.Cosine).ThenBy(e => e.A).ThenBy(e => e.B))
            {
                if (split.Find(a) != split.Find(b) && split.SizeOf(a) + split.SizeOf(b) <= MaxGroupSize)
                {
                    split.Union(a, b);
                }
            }

            clusters.AddRange(component.GroupBy(split.Find).Select(g => g.ToList()).Where(g => g.Count >= 2));
        }

        return [.. clusters.OrderBy(c => c.Min()).Select(c => c.Order().Select(i => items[i]).ToList())];
    }

    private async Task<GroupPlan?> PlanWithModelAsync(Guid projectId, IChatClient chatClient, IReadOnlyList<Item> cluster, CancellationToken ct)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, ReconsolidationPrompt.SystemPrompt),
            new(ChatRole.User, ReconsolidationPrompt.BuildUserPrompt([.. cluster.Select(i => i.Memory)])),
        ];

        try
        {
            var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            await usageRecorder.RecordChatUsageAsync(projectId, null, "MemoryReconsolidation", chatClient, response.Usage, logger, ct).ConfigureAwait(false);
            return Validate(cluster, ReconsolidationPrompt.Parse(response.Text, cluster.Count));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "LLM provider {Provider} failed to reconsolidate a memory group for project {ProjectId}; merging duplicates only.",
                chatClient.GetProviderName(), projectId);
            return null;
        }
    }

    /// <summary>
    /// Turns the model's answer into a plan. The group is ambiguous when a canonical would lose a fact of
    /// one of its sources, when a source is claimed twice, or when the answer is malformed. A canonical
    /// backed by fewer than two sources merges nothing, so those sources stay as they are.
    /// </summary>
    private static GroupPlan Validate(IReadOnlyList<Item> cluster, ReconsolidationResponse response)
    {
        var problems = new List<string>(response.Problems);
        var canonicals = new List<ReconsolidationCanonical>();
        var merged = new HashSet<int>();

        for (var n = 0; n < response.Canonicals.Count; n++)
        {
            var parsed = response.Canonicals[n];
            var sourceIndexes = parsed.Sources.Where(s => s >= 1 && s <= cluster.Count).Select(s => s - 1).Distinct().ToList();
            if (sourceIndexes.Count < 2)
            {
                continue;
            }

            var candidate = MemoryCandidateValidator.Normalize(parsed.Candidate, out var reason, maxFacts: MaxCanonicalFacts);
            if (candidate is null)
            {
                problems.Add($"canonical {n + 1} is not knowledge ({reason})");
                continue;
            }

            foreach (var index in sourceIndexes.Where(i => !merged.Add(i)))
            {
                problems.Add($"source {index + 1} is merged into more than one canonical");
            }

            var sources = sourceIndexes.Select(i => cluster[i]).ToList();
            foreach (var source in sources)
            {
                var lost = source.Facts.FirstOrDefault(fact => !candidate.Facts.Any(kept => Covers(kept, fact)));
                if (lost is not null)
                {
                    problems.Add($"canonical {n + 1} drops a fact of \"{source.Memory.Title}\": {lost}");
                }
            }

            canonicals.Add(ToCanonical(candidate, sources));
        }

        var contradictions = new List<(Item A, Item B, string? Reason)>();
        foreach (var c in response.Contradictions.Where(c => c.A != c.B && InRange(c.A) && InRange(c.B)))
        {
            if (merged.Contains(c.A - 1) || merged.Contains(c.B - 1))
            {
                problems.Add($"sources {c.A} and {c.B} are both merged and contradicted");
                continue;
            }

            var (a, b) = (cluster[c.A - 1], cluster[c.B - 1]);
            if (!contradictions.Any(x => (x.A == a && x.B == b) || (x.A == b && x.B == a)))
            {
                contradictions.Add((a, b, c.Reason));
            }
        }

        var keepSeparate = response.KeepSeparate.Where(InRange).Select(i => i - 1).ToList();
        foreach (var index in keepSeparate.Where(merged.Contains))
        {
            problems.Add($"source {index + 1} is both merged and kept separate");
        }

        return new GroupPlan(
            canonicals,
            contradictions,
            [.. keepSeparate.Select(i => cluster[i].Memory.Id)],
            problems.Count > 0,
            problems.Count > 0 ? string.Join("; ", problems.Distinct()) : null);

        bool InRange(int number) => number >= 1 && number <= cluster.Count;
    }

    /// <summary>Near-duplicate wording, or the source fact contained word for word in a longer canonical fact.</summary>
    private static bool Covers(string canonicalFact, string sourceFact) =>
        FactMerger.AreNearDuplicates(canonicalFact, sourceFact)
        || $" {FactMerger.NormalizeFact(canonicalFact)} ".Contains($" {FactMerger.NormalizeFact(sourceFact)} ", StringComparison.Ordinal);

    /// <summary>Without a model only near-identical memories naming a common strong entity merge: the most
    /// confident one keeps its wording and gains the others' facts and files.</summary>
    private static GroupPlan PlanOffline(IReadOnlyList<Item> cluster, string note)
    {
        var duplicates = new DisjointSets(cluster.Count);
        for (var a = 0; a < cluster.Count; a++)
        {
            for (var b = a + 1; b < cluster.Count; b++)
            {
                if (Cosine(cluster[a], cluster[b]) >= MemoryRelationJudge.OfflineDuplicateCosine
                    && cluster[a].Strong.Overlaps(cluster[b].Strong)
                    && !MemoryRelationJudge.HasNegationAsymmetry(RelationSubject.From(cluster[a].Memory), RelationSubject.From(cluster[b].Memory)))
                {
                    duplicates.Union(a, b);
                }
            }
        }

        var canonicals = new List<ReconsolidationCanonical>();
        foreach (var component in duplicates.Groups().Where(g => g.Count >= 2))
        {
            var sources = component
                .Select(i => cluster[i])
                .OrderByDescending(i => i.Memory.Confidence)
                .ThenByDescending(i => i.Memory.LastReinforcedAt)
                .ToList();
            var facts = sources.Aggregate((IReadOnlyList<string>)[], (acc, s) => FactMerger.Merge(acc, s.Facts));
            if (facts.Count > MaxCanonicalFacts)
            {
                continue;
            }

            var best = sources[0].Memory;
            var candidate = new MemoryCandidate(best.Type, best.Title, best.Body, EvidenceSource.Consolidation, facts, [], [], [], best.Topic);
            canonicals.Add(ToCanonical(candidate, sources));
        }

        return new GroupPlan(canonicals, [], [], false, canonicals.Count > 0 ? note : null);
    }

    /// <summary>The canonical keeps every file its sources name; the model's topic wins, else the sources' most common one.</summary>
    private static ReconsolidationCanonical ToCanonical(MemoryCandidate candidate, IReadOnlyList<Item> sources)
    {
        var modified = MemoryFields.UnionList(
            candidate.FilesModified, sources.SelectMany(s => EntityExtractor.ParseList(s.Memory.FilesModifiedJson)));
        var read = MemoryFields.UnionList(candidate.FilesRead, sources.SelectMany(s => EntityExtractor.ParseList(s.Memory.FilesReadJson)))
            .Where(f => !modified.Contains(f, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var topic = candidate.Topic ?? sources
            .Select(s => s.Memory.Topic)
            .OfType<string>()
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        return new ReconsolidationCanonical(
            candidate.Type,
            candidate.Title,
            candidate.Body,
            topic,
            candidate.Facts,
            candidate.Entities,
            read,
            modified,
            [.. sources.Select(s => s.Memory.Id)]);
    }

    private async Task<List<ReconsolidationContradiction>> CheckContradictionsAsync(
        IReadOnlyList<(Item A, Item B, string? Reason)> contradictions, CancellationToken ct)
    {
        var checks = new Dictionary<Guid, CodeCheck>();
        var result = new List<ReconsolidationContradiction>(contradictions.Count);
        foreach (var (a, b, reason) in contradictions)
        {
            var left = await CheckAsync(a.Memory).ConfigureAwait(false);
            var right = await CheckAsync(b.Memory).ConfigureAwait(false);
            Guid? winner = (left.Result, right.Result) switch
            {
                (CodeCheckResult.Verified, CodeCheckResult.Refuted) => a.Memory.Id,
                (CodeCheckResult.Refuted, CodeCheckResult.Verified) => b.Memory.Id,
                _ => null,
            };
            var note = string.Join("; ", new[] { left.Note, right.Note }.OfType<string>());
            result.Add(new ReconsolidationContradiction(
                a.Memory.Id, b.Memory.Id, reason, new ContradictionCodeCheck(left.Result, right.Result, winner, note.Length > 0 ? note : null)));
        }

        return result;

        async Task<CodeCheck> CheckAsync(DomainMemory memory)
        {
            if (!checks.TryGetValue(memory.Id, out var check))
            {
                checks[memory.Id] = check = await codeChecker.CheckAsync(memory, ct).ConfigureAwait(false);
            }

            return check;
        }
    }

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

        var memory = new DomainMemory
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Type = canonical.Type,
            Status = MemoryStatus.Active,
            Title = canonical.Title,
            Body = canonical.Body,
            Topic = canonical.Topic,
            TopicKey = TopicNormalizer.Normalize(canonical.Topic),
            ScopePath = ScopePathHeuristics.DeriveFromFiles(canonical.FilesModified, canonical.FilesRead) ?? sources.Select(s => s.ScopePath).FirstOrDefault(p => p is not null),
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

        var evidence = new List<MemoryEvidence>();
        foreach (var source in sources)
        {
            var rows = sourceEvidence[source.Id].ToList();
            evidence.AddRange(rows.Select(e => new MemoryEvidence
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                MemoryId = memory.Id,
                Relation = e.Relation,
                Source = e.Source,
                SessionId = e.SessionId,
                SourceMemoryId = e.SourceMemoryId ?? source.Id,
                ObservedAt = e.ObservedAt,
                Note = e.Note,
            }));

            // The link shares the source's first session so it counts as the same independent source.
            evidence.Add(NewEvidence(projectId, memory.Id, EvidenceRelation.Merged, source.Id, now, MergedNote,
                rows.Where(e => e.Source is not EvidenceSource.Code and not EvidenceSource.Explicit && e.SessionId is not null)
                    .OrderBy(e => e.ObservedAt)
                    .Select(e => e.SessionId)
                    .FirstOrDefault()));
        }

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

    private static double Cosine(Item a, Item b) =>
        a.Vector is not null && b.Vector is not null ? CosineSimilarity.Compute(a.Vector, b.Vector) : 0.0;

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

    [GeneratedRegex(@"^\s*changed\s+\d+\s+files?\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChangedFilesTitleRegex();

    private sealed record Item(DomainMemory Memory, HashSet<string> Strong, string? TopicKey, float[]? Vector, IReadOnlyList<string> Facts);

    private sealed record GroupPlan(
        IReadOnlyList<ReconsolidationCanonical> Canonicals,
        IReadOnlyList<(Item A, Item B, string? Reason)> Contradictions,
        IReadOnlyList<Guid> KeepSeparate,
        bool Ambiguous,
        string? Note);

    private sealed class DisjointSets
    {
        private readonly int[] parent;
        private readonly int[] size;

        public DisjointSets(int count)
        {
            parent = [.. Enumerable.Range(0, count)];
            size = [.. Enumerable.Repeat(1, count)];
        }

        public int Find(int x)
        {
            while (parent[x] != x)
            {
                parent[x] = parent[parent[x]];
                x = parent[x];
            }

            return x;
        }

        public int SizeOf(int x) => size[Find(x)];

        public void Union(int a, int b)
        {
            var (ra, rb) = (Find(a), Find(b));
            if (ra == rb)
            {
                return;
            }

            if (size[ra] < size[rb])
            {
                (ra, rb) = (rb, ra);
            }

            parent[rb] = ra;
            size[ra] += size[rb];
        }

        /// <summary>Members of each set, each list ascending, lists ordered by their smallest member.</summary>
        public List<List<int>> Groups() =>
            [.. Enumerable.Range(0, parent.Length).GroupBy(Find).Select(g => g.Order().ToList()).OrderBy(g => g[0])];
    }
}
