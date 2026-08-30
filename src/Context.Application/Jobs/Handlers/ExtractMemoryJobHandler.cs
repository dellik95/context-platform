using System.Text.Json;
using Context.Application.Notifications;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;

namespace Context.Application.Jobs.Handlers;

/// <summary>Extracts durable memories from an already-generated <see cref="SessionSummary"/>.</summary>
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

        var summary = await sessionRepository.GetSummaryAsync(payload.SessionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No SessionSummary found for session {payload.SessionId}; ExtractMemory must run after SummarizeSession.");

        // IMemoryExtractor.ExtractAsync writes new/reinforced/superseded memories itself via
        // IMemoryStore - nothing further to persist here.
        await extractor.ExtractAsync(summary, ct).ConfigureAwait(false);

        // SessionSummary carries no ProjectId of its own - the owning Session is the only path to it.
        var session = await sessionRepository.GetByIdAsync(payload.SessionId, ct).ConfigureAwait(false);
        if (session is not null)
        {
            await _activityNotifier.NotifyAsync(session.ProjectId, ProjectActivityKind.MemoryCreated, ct).ConfigureAwait(false);
        }
    }
}
