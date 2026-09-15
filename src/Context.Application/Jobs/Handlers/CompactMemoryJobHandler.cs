using System.Text.Json;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// Periodic memory compaction, per <see cref="CompactMemoryPayload"/>: clusters a project's
/// active, non-pinned, not-yet-compacted memories by embedding cosine similarity (threshold from
/// <see cref="ProjectSettingsDto.MemoryCompactionSimilarityThreshold"/>) and merges each cluster of
/// two or more into one consolidated <see cref="Memory"/> via <see cref="MemoryMergePrompt"/>. Each
/// merged memory is marked <see cref="Memory.IsCompacted"/> so a later pass never re-merges it; its
/// sources are kept but marked superseded, same as <see cref="Context.Memory.Extraction.MemoryConsolidator"/>'s
/// supersede path. Fired by <see cref="Scheduling.MemoryCompactionScheduler"/>.
/// </summary>
public sealed class CompactMemoryJobHandler(
    IMemoryStore memoryStore,
    IProjectSettingsService settingsService,
    IProviderResolver providerResolver,
    Context.Memory.Abstractions.IUsageRecorder usageRecorder) : IJobHandler
{
    public JobType Type => JobType.CompactMemory;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<CompactMemoryPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(CompactMemoryPayload)}.");

        var settings = await settingsService.GetAsync(payload.ProjectId, ct).ConfigureAwait(false);

        var candidates = (await memoryStore.GetExistingWithEmbeddingsAsync(payload.ProjectId, ct).ConfigureAwait(false))
            .Where(m => !m.Memory.Pinned && !m.Memory.IsCompacted)
            .ToList();

        var clusters = ClusterBySimilarity(candidates, settings.MemoryCompactionSimilarityThreshold);

        var chatClient = await providerResolver.GetChatClientAsync(payload.ProjectId, ct).ConfigureAwait(false);
        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(payload.ProjectId, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        foreach (var cluster in clusters)
        {
            if (cluster.Count <= 1)
            {
                continue; // singleton - nothing to merge
            }

            ct.ThrowIfCancellationRequested();

            var sources = cluster.Select(c => c.Memory).ToList();
            var (title, body) = await ResolveMergeAsync(payload.ProjectId, sources, chatClient, ct).ConfigureAwait(false);

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
            await memoryStore.SaveAsync(merged, embeddingResult.Vector.ToArray(), ct).ConfigureAwait(false);

            foreach (var source in sources)
            {
                await memoryStore.MarkSupersededAsync(source.Id, merged.Id, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Greedy single-pass clustering: each not-yet-assigned candidate seeds a new
    /// cluster, which then absorbs every other not-yet-assigned candidate within
    /// <paramref name="threshold"/> cosine similarity of it. Not a full transitive-closure
    /// clustering (a candidate is only compared against the cluster's seed, not every member
    /// already added) - deliberately simple, same posture as the rest of this job's fail-open
    /// design.</summary>
    private static List<List<ExistingMemoryWithEmbedding>> ClusterBySimilarity(
        List<ExistingMemoryWithEmbedding> candidates, double threshold)
    {
        var clusters = new List<List<ExistingMemoryWithEmbedding>>();
        var assigned = new HashSet<Guid>();

        foreach (var candidate in candidates)
        {
            if (assigned.Contains(candidate.Memory.Id))
            {
                continue;
            }

            var cluster = new List<ExistingMemoryWithEmbedding> { candidate };
            assigned.Add(candidate.Memory.Id);

            foreach (var other in candidates)
            {
                if (assigned.Contains(other.Memory.Id))
                {
                    continue;
                }

                if (CosineSimilarity.Compute(candidate.Embedding, other.Embedding) >= threshold)
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
            await RecordUsageAsync(projectId, chatClient, response.Usage, ct).ConfigureAwait(false);
            return MemoryMergePrompt.ParseResponse(response.Text, sources);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return MemoryMergePrompt.HeuristicMerge(sources);
        }
    }

    // Best-effort: a failure recording token usage must not affect the merge itself, so every
    // exception here (other than cancellation) is swallowed rather than propagated.
    private async Task RecordUsageAsync(Guid projectId, IChatClient chatClient, UsageDetails? usage, CancellationToken ct)
    {
        try
        {
            await usageRecorder.RecordAsync(new UsageRecord
            {
                ProjectId = projectId,
                SessionId = null,
                Purpose = "MemoryCompaction",
                Provider = chatClient.GetProviderName(),
                Model = chatClient.GetModelId(),
                PromptTokens = (int?)usage?.InputTokenCount,
                CompletionTokens = (int?)usage?.OutputTokenCount,
                TotalTokens = (int?)usage?.TotalTokenCount,
                CreatedAt = DateTimeOffset.UtcNow
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Swallow - see remarks above.
        }
    }

    private static string? UnionJsonArray(IEnumerable<string?> jsonArrays)
    {
        var union = jsonArrays
            .Where(json => !string.IsNullOrWhiteSpace(json))
            .SelectMany(json => JsonSerializer.Deserialize<List<string>>(json!) ?? [])
            .Distinct()
            .ToList();

        return union.Count > 0 ? JsonSerializer.Serialize(union) : null;
    }
}
