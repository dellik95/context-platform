using System.Text.Json;
using Context.Application.Abstractions;
using Context.Application.Jobs;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Providers;
using Context.Providers.Abstractions;
using Context.Retrieval.Scoring;
using Context.Shared;
using Microsoft.Extensions.AI;

namespace Context.Application.Engine;

/// <summary>
/// Manages session lifecycle (start, event recording, compact extraction, end, summary jobs) and
/// hybrid session-summary search for <see cref="ContextEngine"/>.
/// </summary>
internal sealed class SessionCoordinator(
    ISessionRepository sessionRepository,
    ISessionSummaryVectorIndex sessionSummaryVectorIndex,
    IProviderResolver providerResolver,
    IProjectSettingsService projectSettingsService,
    IUnitOfWork unitOfWork,
    IJobRepository jobRepository,
    IJobQueue jobQueue)
{
    private const int SemanticSearchLimit = 50;

    public async Task<IReadOnlyList<SessionSummaryChunk>> SearchSessionSummariesAsync(
        Guid projectId, string query, CancellationToken ct, Guid? sessionId = null, int limit = 20)
    {
        var lexicalHits = await sessionRepository.SearchSummaryChunksAsync(projectId, query, limit, ct, sessionId).ConfigureAwait(false);
        var lexicalIds = lexicalHits.Select(c => c.Id).ToHashSet();

        var semanticScores = await SemanticSearchSessionSummariesAsync(projectId, query, sessionId, ct).ConfigureAwait(false);

        var byId = lexicalHits.ToDictionary(c => c.Id);
        foreach (var id in semanticScores.Keys.Where(id => !byId.ContainsKey(id)).ToList())
        {
            var chunk = await sessionRepository.GetSummaryChunkByIdAsync(projectId, id, ct).ConfigureAwait(false);
            if (chunk is not null && (sessionId is null || chunk.SessionId == sessionId))
            {
                byId[chunk.Id] = chunk;
            }
        }

        if (byId.Count == 0)
        {
            return [];
        }

        var normalizedSemantic = NormalizeSemanticScores(semanticScores);
        return [.. byId.Values.OrderByDescending(c => CombinedChunkScore(c, lexicalIds, normalizedSemantic)).Take(limit)];
    }

    public async Task<Guid> StartSessionAsync(Guid projectId, string? claudeSessionId, CancellationToken ct, string? cwd = null)
    {
        if (claudeSessionId is not null)
        {
            var existing = await sessionRepository.GetByClaudeSessionIdAsync(projectId, claudeSessionId, ct).ConfigureAwait(false);
            if (existing is not null && existing.ProjectId == projectId)
            {
                var revived = existing.Status == SessionStatus.Ended;
                var gainedCwd = existing.Cwd is null && cwd is not null;
                if (revived || gainedCwd)
                {
                    if (revived)
                    {
                        existing.Status = SessionStatus.Active;
                        existing.EndedAt = null;
                    }

                    existing.Cwd ??= cwd;
                    await sessionRepository.UpdateAsync(existing, ct).ConfigureAwait(false);
                }

                return existing.Id;
            }
        }

        var session = new Session
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            ClaudeSessionId = claudeSessionId,
            StartedAt = DateTimeOffset.UtcNow,
            EndedAt = null,
            Status = SessionStatus.Active,
            Cwd = cwd
        };

        var saved = await sessionRepository.AddAsync(session, ct).ConfigureAwait(false);
        return saved.Id;
    }

    public async Task RecordEventAsync(
        Guid projectId, Guid sessionId, SessionEventType type, object payload, CancellationToken ct,
        Guid? eventId = null, DateTimeOffset? timestamp = null)
    {
        var payloadJson = JsonSerializer.Serialize(payload, payload.GetType());

        var sessionEvent = new SessionEvent
        {
            Id = eventId ?? Guid.NewGuid(),
            SessionId = sessionId,
            Type = type,
            PayloadJson = payloadJson,
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            TokenEstimate = TokenEstimator.Estimate(payloadJson)
        };

        var settings = await projectSettingsService.GetAsync(projectId, ct).ConfigureAwait(false);
        var queued = await unitOfWork.InTransactionAsync(async () =>
        {
            await sessionRepository.AddEventAsync(sessionEvent, ct).ConfigureAwait(false);
            return await QueueExtractionBatchAsync(projectId, sessionId, Math.Max(1, settings.MinEventsForMemoryExtraction), ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        if (queued)
        {
            await WakeAfterCommitAsync(projectId, ct).ConfigureAwait(false);
        }
    }

    public async Task ExtractMemoryFromCompactSummaryAsync(Guid projectId, Guid sessionId, string compactSummary, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(compactSummary))
        {
            return;
        }

        await InsertJobAsync(JobType.ExtractMemory, new ExtractMemoryPayload(sessionId, CompactSummary: compactSummary), projectId, ct).ConfigureAwait(false);
        await WakeAfterCommitAsync(projectId, ct).ConfigureAwait(false);
    }

    public async Task EndSessionAsync(Guid projectId, Guid sessionId, CancellationToken ct, string? reason = null)
    {
        var ownerProjectId = await unitOfWork.InTransactionAsync(async () =>
        {
            var session = await sessionRepository.GetByIdAsync(projectId, sessionId, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");

            session.Status = SessionStatus.Ended;
            session.EndedAt = DateTimeOffset.UtcNow;
            session.EndReason = reason;
            await sessionRepository.UpdateAsync(session, ct).ConfigureAwait(false);

            await QueueExtractionBatchAsync(session.ProjectId, sessionId, minEvents: 1, ct).ConfigureAwait(false);
            await InsertJobAsync(JobType.SummarizeSession, new SummarizeSessionPayload(sessionId, IsSessionEnd: true), session.ProjectId, ct).ConfigureAwait(false);
            return session.ProjectId;
        }, ct).ConfigureAwait(false);

        await WakeAfterCommitAsync(ownerProjectId, ct).ConfigureAwait(false);
    }

    public async Task RequestSessionSummaryAsync(Guid projectId, Guid sessionId, CancellationToken ct)
    {
        var session = await sessionRepository.GetByIdAsync(projectId, sessionId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");

        await InsertJobAsync(JobType.SummarizeSession, new SummarizeSessionPayload(sessionId, IsSessionEnd: false), session.ProjectId, ct).ConfigureAwait(false);
        await WakeAfterCommitAsync(session.ProjectId, ct).ConfigureAwait(false);
    }

    private async Task<Dictionary<Guid, double>> SemanticSearchSessionSummariesAsync(Guid projectId, string query, Guid? sessionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
        var embeddingResult = await embeddingGenerator.GenerateAsync(query, MemorySearchHelper.InteractiveEmbeddingOptions, ct).ConfigureAwait(false);

        var hits = await sessionSummaryVectorIndex.SearchAsync(
            projectId, embeddingGenerator.GetProviderModelKey(), embeddingResult.Vector.ToArray(), SemanticSearchLimit, ct, sessionId)
            .ConfigureAwait(false);

        return hits.ToDictionary(h => h.ChunkId, h => h.Score);
    }

    private static IReadOnlyDictionary<Guid, double> NormalizeSemanticScores(Dictionary<Guid, double> raw)
    {
        if (raw.Count == 0)
        {
            return raw;
        }

        var ids = raw.Keys.ToList();
        var normalized = HybridScorer.NormalizeMinMax([.. ids.Select(id => raw[id])]);

        var result = new Dictionary<Guid, double>(raw.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            result[ids[i]] = normalized[i];
        }

        return result;
    }

    private static double CombinedChunkScore(SessionSummaryChunk chunk, HashSet<Guid> lexicalIds, IReadOnlyDictionary<Guid, double> normalizedSemantic)
    {
        var semanticScore = normalizedSemantic.TryGetValue(chunk.Id, out var s) ? s : 0.0;
        return lexicalIds.Contains(chunk.Id) ? Math.Max(semanticScore, 1.0) : semanticScore;
    }

    private Task WakeAfterCommitAsync(Guid projectId, CancellationToken ct) =>
        unitOfWork.InTransaction ? Task.CompletedTask : jobQueue.WakeAsync(projectId, ct);

    private async Task<bool> QueueExtractionBatchAsync(Guid projectId, Guid sessionId, int minEvents, CancellationToken ct)
    {
        var batchId = await sessionRepository.ClaimEventsForExtractionAsync(sessionId, minEvents, ct).ConfigureAwait(false);
        if (batchId is null)
        {
            return false;
        }

        await InsertJobAsync(JobType.ExtractMemory, new ExtractMemoryPayload(sessionId, EventBatchId: batchId), projectId, ct).ConfigureAwait(false);
        return true;
    }

    private Task InsertJobAsync(JobType type, object payload, Guid projectId, CancellationToken ct) =>
        jobRepository.EnqueueAsync(JobFactory.Create(type, payload, projectId, JobPriority.Session), ct);
}
