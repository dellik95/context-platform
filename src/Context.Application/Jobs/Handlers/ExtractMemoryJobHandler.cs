using System.Text.Json;
using Context.Application.Notifications;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;

namespace Context.Application.Jobs.Handlers;

/// <summary>Extracts durable memories from the single <see cref="SessionSummaryChunk"/> a
/// <see cref="SummarizeSessionJobHandler"/> pass just produced - not the session's full merged
/// history, so a periodic mid-session pass only ever re-examines what's new since the last one.</summary>
public sealed class ExtractMemoryJobHandler(
    IMemoryExtractor extractor,
    ISessionRepository sessionRepository,
    IProjectActivityNotifier? activityNotifier = null) : IJobHandler
{
    private readonly IProjectActivityNotifier _activityNotifier = activityNotifier ?? NullProjectActivityNotifier.Instance;

    public JobType Type => JobType.ExtractMemory;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ExtractMemoryPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(ExtractMemoryPayload)}.");

        // ExtractMemory jobs are always enqueued with the owning session's ProjectId (see
        // SummarizeSessionJobHandler) - required here to scope the now project-checked
        // ISessionRepository lookups below.
        var projectId = job.ProjectId
            ?? throw new InvalidOperationException($"Job {job.Id}: {nameof(JobType)}.{nameof(JobType.ExtractMemory)} jobs must carry a ProjectId.");

        var chunk = await sessionRepository.GetSummaryChunkByIdAsync(projectId, payload.SummaryChunkId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No SessionSummaryChunk {payload.SummaryChunkId} found for session {payload.SessionId}; ExtractMemory must run after SummarizeSession.");

        // IMemoryExtractor.ExtractAsync writes new/reinforced/superseded memories itself via
        // IMemoryStore - nothing further to persist here.
        await extractor.ExtractAsync(projectId, chunk, ct).ConfigureAwait(false);

        // SessionSummaryChunk carries no ProjectId of its own - the owning Session is the only path to it.
        var session = await sessionRepository.GetByIdAsync(projectId, payload.SessionId, ct).ConfigureAwait(false);
        if (session is not null)
        {
            await _activityNotifier.NotifyAsync(session.ProjectId, ProjectActivityKind.MemoryCreated, ct).ConfigureAwait(false);
        }
    }
}
