using Context.Application.Jobs;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Context.Memory.Consolidation;
using Context.Memory.Extraction;
using Context.Memory.Search;

namespace Context.Application.Engine;

/// <summary>
/// Handles memory queries, inspection, explicit remembers, and forget requests for
/// <see cref="ContextEngine"/>.
/// </summary>
internal sealed class MemoryCoordinator(
    IMemoryRepository memoryRepository,
    IMemoryStore memoryStore,
    IMemoryConsolidator memoryConsolidator,
    IJobQueue jobQueue,
    MemorySearchHelper memorySearch)
{
    private const int ExplicitRememberTitleCharLimit = 80;

    public async Task<IReadOnlyList<Context.Domain.Entities.Memory>> SearchMemoryAsync(
        Guid projectId, string query, bool includeSuperseded, CancellationToken ct, string? topic = null)
    {
        var memories = await memoryRepository.ListByProjectAsync(projectId, includeSuperseded, ct).ConfigureAwait(false);
        var topicFilter = string.IsNullOrWhiteSpace(topic) ? null : topic.Trim();
        var pool = memories
            .Where(m => topicFilter is null || string.Equals(m.Topic?.Trim(), topicFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (pool.Count == 0)
        {
            return [];
        }

        var parsed = MemoryQuery.Parse(query);
        var matches = await memorySearch.MatchAsync(projectId, parsed, pool, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        return [.. pool
            .Where(m => !parsed.HasSubject || matches.ContainsKey(m.Id))
            .OrderByDescending(m => MemorySearchHelper.Score(m, matches.GetValueOrDefault(m.Id), now))];
    }

    public async Task<MemoryDetail?> GetMemoryAsync(Guid projectId, Guid memoryId, CancellationToken ct)
    {
        var memory = await memoryRepository.GetByIdAsync(memoryId, ct).ConfigureAwait(false);
        if (memory is null || memory.ProjectId != projectId)
        {
            return null;
        }

        var evidence = await memoryRepository.ListEvidenceAsync([memoryId], ct).ConfigureAwait(false);
        var entities = await memoryRepository.ListEntitiesAsync([memoryId], ct).ConfigureAwait(false);
        return new MemoryDetail(memory, evidence, entities);
    }

    public async Task<bool> ForgetMemoryAsync(Guid projectId, Guid memoryId, CancellationToken ct)
    {
        var memory = await memoryRepository.GetByIdAsync(memoryId, ct).ConfigureAwait(false);
        if (memory is null || memory.ProjectId != projectId)
        {
            return false;
        }

        return await MemoryContests.ForgetAsync(memoryStore, memoryId, ct).ConfigureAwait(false);
    }

    public async Task<Guid?> RequestForgetAsync(Guid projectId, Guid memoryId, CancellationToken ct)
    {
        var memory = await memoryRepository.GetByIdAsync(memoryId, ct).ConfigureAwait(false);
        if (memory is null || memory.ProjectId != projectId)
        {
            return null;
        }

        return await jobQueue.EnqueueAsync(
            JobType.ForgetMemory, new ForgetMemoryPayload(memoryId), projectId, JobPriority.Session, ct).ConfigureAwait(false);
    }

    public async Task<Context.Domain.Entities.Memory> RememberExplicitlyAsync(
        Guid projectId,
        string content,
        MemoryType? type,
        CancellationToken ct,
        string? title = null,
        string? topic = null,
        IReadOnlyList<string>? facts = null,
        IReadOnlyList<string>? concepts = null,
        IReadOnlyList<string>? filesRead = null,
        IReadOnlyList<string>? filesModified = null)
    {
        var candidate = BuildExplicitCandidate(content, type, title, topic, facts, concepts, filesRead, filesModified);
        var result = await memoryConsolidator.ConsolidateAsync(projectId, sessionId: null, candidate, isInteractive: true, ct).ConfigureAwait(false);
        return result.Memory;
    }

    public async Task<Guid> RequestRememberAsync(
        Guid projectId,
        string content,
        MemoryType? type,
        CancellationToken ct,
        string? title = null,
        string? topic = null,
        IReadOnlyList<string>? facts = null,
        IReadOnlyList<string>? concepts = null,
        IReadOnlyList<string>? filesRead = null,
        IReadOnlyList<string>? filesModified = null)
    {
        BuildExplicitCandidate(content, type, title, topic, facts, concepts, filesRead, filesModified);

        var payload = new RememberMemoryPayload(content, type, title, topic, facts, concepts, filesRead, filesModified);
        return await jobQueue.EnqueueAsync(JobType.RememberMemory, payload, projectId, JobPriority.Session, ct).ConfigureAwait(false);
    }

    private static MemoryCandidate BuildExplicitCandidate(
        string content,
        MemoryType? type,
        string? title,
        string? topic,
        IReadOnlyList<string>? facts,
        IReadOnlyList<string>? concepts,
        IReadOnlyList<string>? filesRead,
        IReadOnlyList<string>? filesModified)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        return new MemoryCandidate(
            Type: type ?? MemoryType.Fact,
            Title: string.IsNullOrWhiteSpace(title) ? BuildExplicitRememberTitle(content) : title.Trim(),
            Body: content,
            Source: EvidenceSource.Explicit,
            Facts: facts ?? [],
            Concepts: concepts ?? [],
            FilesRead: filesRead ?? [],
            FilesModified: filesModified ?? [],
            Topic: string.IsNullOrWhiteSpace(topic) ? null : topic.Trim(),
            ScopePath: ScopePathHeuristics.DeriveFromFiles(filesModified ?? [], filesRead ?? []));
    }

    private static string BuildExplicitRememberTitle(string content)
    {
        var trimmed = content.Trim();
        var sentenceEnd = trimmed.IndexOfAny(['.', '!', '?']);
        if (sentenceEnd > 0 && sentenceEnd < ExplicitRememberTitleCharLimit)
        {
            return trimmed[..(sentenceEnd + 1)];
        }

        return trimmed.Length <= ExplicitRememberTitleCharLimit
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, ExplicitRememberTitleCharLimit), "…");
    }
}
