using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using Context.Providers;
using Context.Providers.Abstractions;
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
public sealed class MemoryReconsolidator(
    IMemoryStore store,
    ICodeVerifier codeVerifier,
    IProviderResolver providerResolver,
    IUsageRecorder usageRecorder,
    IMemoryMergeOptionsProvider mergeOptionsProvider,
    ILogger<MemoryReconsolidator>? logger = null)
{
    public const int EdgeMinSharedStrong = 2;

    private readonly ReconsolidationPlanner planner = new(codeVerifier, usageRecorder, logger);
    private readonly ReconsolidationApplier applier = new(store, providerResolver);

    public async Task<ReconsolidationReport> PlanAsync(Guid projectId, CancellationToken ct)
    {
        var options = await mergeOptionsProvider.GetAsync(projectId, ct).ConfigureAwait(false);
        var live = (await store.ListLiveKnowledgeAsync(projectId, ct).ConfigureAwait(false))
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .ToList();
        var evidence = (await store.ListEvidenceAsync([.. live.Select(m => m.Id)], ct).ConfigureAwait(false)).ToLookup(e => e.MemoryId);

        var (changeLogs, knowledge) = PartitionKnowledgeAndChangeLogs(live, evidence);
        var (items, reembedded) = await BuildItemsAsync(projectId, knowledge, ct).ConfigureAwait(false);

        var chatClient = await providerResolver.GetChatClientAsync(projectId, ct).ConfigureAwait(false);
        var usedModel = false;
        var groups = new List<ReconsolidationGroup>();

        foreach (var cluster in ReconsolidationClusterer.Cluster(items, options))
        {
            ReconsolidationGroupPlan? plan = null;
            if (!chatClient.IsNoOp())
            {
                plan = await planner.PlanWithModelAsync(projectId, chatClient, cluster, options, ct).ConfigureAwait(false);
                usedModel |= plan is not null;
            }

            plan ??= ReconsolidationPlanner.PlanOffline(
                cluster,
                chatClient.IsNoOp()
                    ? "offline: no chat model, duplicates merged by fact union"
                    : "offline: the chat model failed, duplicates merged by fact union",
                options);
            if (plan.Canonicals.Count == 0 && plan.Contradictions.Count == 0 && !plan.Ambiguous)
            {
                continue;
            }

            groups.Add(new ReconsolidationGroup(
                groups.Count + 1,
                [.. cluster.Select(i => new ReconsolidationSource(i.Memory.Id, i.Memory.Title, i.Memory.Type, i.Memory.Status, i.Memory.LastReinforcedAt))],
                plan.Canonicals,
                await planner.CheckContradictionsAsync(plan.Contradictions, ct).ConfigureAwait(false),
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
    public Task<ReconsolidationReport> ApplyAsync(
        Guid projectId, ReconsolidationReport report, ReconsolidationApplyOptions options, CancellationToken ct) =>
        applier.ApplyAsync(projectId, report, options, ct);

    private static (List<RetiredChangeLog> ChangeLogs, List<DomainMemory> Knowledge) PartitionKnowledgeAndChangeLogs(
        List<DomainMemory> live, ILookup<Guid, Domain.Entities.MemoryEvidence> evidence)
    {
        var changeLogs = new List<RetiredChangeLog>();
        var knowledge = new List<DomainMemory>();
        foreach (var memory in live)
        {
            if (ReconsolidationClusterer.ChangeLogReason(memory, evidence[memory.Id]) is { } reason)
            {
                changeLogs.Add(new RetiredChangeLog(memory.Id, memory.Title, memory.Status, memory.LastReinforcedAt, reason));
            }
            else
            {
                knowledge.Add(memory);
            }
        }

        return (changeLogs, knowledge);
    }

    private async Task<(List<ReconsolidationItem> Items, int Reembedded)> BuildItemsAsync(
        Guid projectId, List<DomainMemory> knowledge, CancellationToken ct)
    {
        var generator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
        var (vectors, reembedded) = await MemoryVectors.CurrentAsync(
            store, knowledge, generator.GetProviderModelKey(), null, generator, null, ct).ConfigureAwait(false);
        var entityRows = (await store.ListEntitiesAsync([.. knowledge.Select(m => m.Id)], ct).ConfigureAwait(false)).ToLookup(r => r.MemoryId);

        var items = knowledge.Select(m =>
        {
            var rows = entityRows[m.Id].ToList();
            IReadOnlyList<ExtractedEntity> entities = rows.Count > 0 ? [.. rows.Select(r => new ExtractedEntity(r.Key, r.Display))] : EntityExtractor.Extract(m);
            return new ReconsolidationItem(
                m,
                EntityExtractor.StrongKeys(entities.Select(e => e.Key)),
                m.TopicKey ?? TopicNormalizer.Normalize(m.Topic),
                vectors.GetValueOrDefault(m.Id),
                EntityExtractor.ParseList(m.FactsJson));
        }).ToList();

        return (items, reembedded);
    }
}
