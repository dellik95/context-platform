using System.Text.Json;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// Periodic memory compaction, per <see cref="CompactMemoryPayload"/>: clusters a project's
/// active, non-pinned, not-yet-compacted memories by embedding cosine similarity (threshold from
/// <see cref="ProjectSettingsDto.MemoryCompactionSimilarityThreshold"/>) and merges each cluster of
/// two or more into one consolidated <see cref="Memory"/> via <see cref="MemoryMergePrompt"/>. Each
/// merged memory is marked <see cref="Memory.IsCompacted"/> so a later pass never re-merges it; its
/// sources are kept but marked superseded. Fired by <see cref="Scheduling.MemoryCompactionScheduler"/>.
/// </summary>
public sealed class CompactMemoryJobHandler(
    IMemoryStore memoryStore,
    IProjectSettingsService settingsService,
    IProviderResolver providerResolver,
    Context.Memory.Abstractions.IUsageRecorder usageRecorder,
    ILogger<CompactMemoryJobHandler>? logger = null) : IJobHandler
{
    /// <summary>
    /// Upper bound on how many memories a single merge prompt may cover. A runaway cluster would
    /// otherwise build an unbounded prompt and collapse a large slice of project knowledge into one
    /// row; oversized clusters are split into chunks of this size, each merged on its own.
    /// </summary>
    private const int MaxClusterSize = 8;

    public JobType Type => JobType.CompactMemory;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<CompactMemoryPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(CompactMemoryPayload)}.");

        var settings = await settingsService.GetAsync(payload.ProjectId, ct).ConfigureAwait(false);

        var candidates = (await memoryStore.GetExistingWithEmbeddingsAsync(payload.ProjectId, ct).ConfigureAwait(false))
            .Where(m => !m.Memory.Pinned && !m.Memory.IsCompacted)
            .ToList();

        var clusters = ClusterBySimilarity(candidates, settings.MemoryCompactionSimilarityThreshold)
            .Where(c => c.Count > 1)
            .ToList();

        if (clusters.Count == 0)
        {
            logger?.LogInformation(
                "Memory compaction for project {ProjectId}: {CandidateCount} candidates, no mergeable clusters at threshold {Threshold}.",
                payload.ProjectId, candidates.Count, settings.MemoryCompactionSimilarityThreshold);
            return;
        }

        var chatClient = await providerResolver.GetChatClientAsync(payload.ProjectId, ct).ConfigureAwait(false);
        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(payload.ProjectId, ct).ConfigureAwait(false);

        int mergedCount = 0, supersededCount = 0, failedClusters = 0;

        foreach (var cluster in clusters)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                supersededCount += await MergeClusterAsync(payload.ProjectId, cluster, chatClient, embeddingGenerator, ct).ConfigureAwait(false);
                mergedCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad cluster must not abandon the rest of the project's compaction pass; the
                // sources it failed on stay active and are retried by the next scheduled run.
                failedClusters++;
                logger?.LogWarning(ex, "Memory compaction: cluster of {Size} memories failed to merge for project {ProjectId}.",
                    cluster.Count, payload.ProjectId);
            }
        }

        logger?.LogInformation(
            "Memory compaction for project {ProjectId}: {CandidateCount} candidates, {MergedCount} clusters merged, {SupersededCount} memories superseded, {FailedClusters} clusters failed.",
            payload.ProjectId, candidates.Count, mergedCount, supersededCount, failedClusters);
    }

    private async Task<int> MergeClusterAsync(
        Guid projectId,
        List<ExistingMemoryWithEmbedding> cluster,
        IChatClient chatClient,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        CancellationToken ct)
    {
        var sources = cluster.Select(c => c.Memory).ToList();
        var (title, body) = await ResolveMergeAsync(projectId, sources, chatClient, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        var merged = new Context.Domain.Entities.Memory
        {
            Id = Guid.NewGuid(),
            ProjectId = sources[0].ProjectId,
            Type = sources.OrderByDescending(s => s.Confidence).First().Type,
            Title = title,
            Body = body,
            Topic = sources.Select(s => s.Topic).FirstOrDefault(t => t is not null),
            ScopePath = sources.Select(s => s.ScopePath).FirstOrDefault(p => p is not null),
            FactsJson = UnionJsonArray(sources.Select(s => s.FactsJson)),
            ConceptsJson = UnionJsonArray(sources.Select(s => s.ConceptsJson)),
            FilesReadJson = UnionJsonArray(sources.Select(s => s.FilesReadJson)),
            FilesModifiedJson = UnionJsonArray(sources.Select(s => s.FilesModifiedJson)),
            Confidence = sources.Max(s => s.Confidence),
            Tier = sources.Any(s => s.Tier == MemoryTier.LongTerm) ? MemoryTier.LongTerm : MemoryTier.Working,
            Pinned = false,
            ReinforcementCount = sources.Sum(s => s.ReinforcementCount),
            SupersededById = null,
            IsCompacted = true,
            CreatedFromSessionId = null,
            CreatedAt = now,
            LastReinforcedAt = now
        };

        var embeddingResult = await embeddingGenerator.GenerateAsync($"{merged.Title}\n{merged.Body}", cancellationToken: ct).ConfigureAwait(false);
        await memoryStore.SaveMergedAsync(merged, embeddingResult.Vector.ToArray(), [.. sources.Select(s => s.Id)], ct).ConfigureAwait(false);

        return sources.Count;
    }

    /// <summary>
    /// Complete-linkage clustering: a candidate joins a cluster only when it is within
    /// <paramref name="threshold"/> cosine similarity of <em>every</em> member already in it, so a
    /// cluster is always a set of memories that are mutually near-duplicates.
    /// </summary>
    /// <remarks>
    /// Single-linkage (comparing against the seed alone) lets a chain A~B~C form where A and C are
    /// unrelated, and the merge prompt then collapses two distinct facts into one row that can
    /// never be split again — the one failure mode of this job that actually destroys knowledge.
    /// Candidates are ordered oldest-first so the result does not depend on the row order the store
    /// happened to return, and a cluster stops growing at <see cref="MaxClusterSize"/>; the members
    /// it turned away stay unassigned and seed a cluster of their own in the same pass.
    /// </remarks>
    private static List<List<ExistingMemoryWithEmbedding>> ClusterBySimilarity(
        List<ExistingMemoryWithEmbedding> candidates, double threshold)
    {
        var ordered = candidates.OrderBy(c => c.Memory.CreatedAt).ThenBy(c => c.Memory.Id).ToList();
        var clusters = new List<List<ExistingMemoryWithEmbedding>>();
        var assigned = new HashSet<Guid>();

        foreach (var seed in ordered)
        {
            if (!assigned.Add(seed.Memory.Id))
            {
                continue;
            }

            var cluster = new List<ExistingMemoryWithEmbedding> { seed };

            foreach (var other in ordered)
            {
                if (cluster.Count == MaxClusterSize)
                {
                    break;
                }

                if (assigned.Contains(other.Memory.Id))
                {
                    continue;
                }

                if (cluster.All(member => CosineSimilarity.Compute(member.Embedding, other.Embedding) >= threshold))
                {
                    cluster.Add(other);
                    assigned.Add(other.Memory.Id);
                }
            }

            clusters.Add(cluster);
        }

        return clusters;
    }

    private async Task<(string Title, string Body)> ResolveMergeAsync(
        Guid projectId, List<Context.Domain.Entities.Memory> sources, IChatClient chatClient, CancellationToken ct)
    {
        if (chatClient.IsNoOp())
        {
            return MemoryMergePrompt.HeuristicMerge(sources);
        }

        List<ChatMessage> messages =
        [
            new(ChatRole.System, MemoryMergePrompt.SystemPrompt),
            new(ChatRole.User, MemoryMergePrompt.BuildUserPrompt(sources)),
        ];

        try
        {
            var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            await usageRecorder.RecordChatUsageAsync(projectId, null, "MemoryCompaction", chatClient, response.Usage, logger, ct).ConfigureAwait(false);
            return MemoryMergePrompt.ParseResponse(response.Text, sources);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "LLM provider {Provider} failed to merge {SourceCount} memories; falling back to the heuristic merge.",
                chatClient.GetProviderName(), sources.Count);
            return MemoryMergePrompt.HeuristicMerge(sources);
        }
    }

    // A single malformed *Json column must not fail the whole merge, so unparsable arrays are
    // skipped rather than propagated.
    private string? UnionJsonArray(IEnumerable<string?> jsonArrays)
    {
        var union = new List<string>();

        foreach (var json in jsonArrays)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            try
            {
                union.AddRange(JsonSerializer.Deserialize<List<string>>(json) ?? []);
            }
            catch (JsonException ex)
            {
                logger?.LogWarning(ex, "Memory compaction: skipping a malformed JSON array column while merging.");
            }
        }

        var distinct = union.Distinct().ToList();
        return distinct.Count > 0 ? JsonSerializer.Serialize(distinct) : null;
    }
}
