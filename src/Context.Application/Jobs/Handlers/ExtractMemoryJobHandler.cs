using System.Text.Json;
using Context.Application.Notifications;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Application.Jobs.Handlers;

/// <summary>Extracts durable memories from one piece of session material: a claimed batch of raw
/// SessionEvents or a /compact conversation summary.</summary>
public sealed class ExtractMemoryJobHandler(
    IMemoryExtractor extractor,
    ISessionRepository sessionRepository,
    IProjectActivityNotifier? activityNotifier = null,
    ILogger<ExtractMemoryJobHandler>? logger = null,
    IProjectSettingsService? settingsService = null) : IJobHandler
{
    private readonly IProjectActivityNotifier _activityNotifier = activityNotifier ?? NullProjectActivityNotifier.Instance;
    private readonly ILogger<ExtractMemoryJobHandler> _logger = logger ?? NullLogger<ExtractMemoryJobHandler>.Instance;

    public JobType Type => JobType.ExtractMemory;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ExtractMemoryPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(ExtractMemoryPayload)}.");

        var projectId = job.ProjectId
            ?? throw new InvalidOperationException($"Job {job.Id}: {nameof(JobType)}.{nameof(JobType.ExtractMemory)} jobs must carry a ProjectId.");

        // A retried job re-proposes the same candidates; consolidation reinforces them in place (same
        // session, so SourceCount does not grow). That is a side effect, not an idempotency guarantee.
        IReadOnlyList<Context.Domain.Entities.Memory> memories;
        string source;
        if (payload.EventBatchId is { } batchId)
        {
            var events = await sessionRepository.GetEventsByExtractionBatchAsync(projectId, payload.SessionId, batchId, ct).ConfigureAwait(false);
            if (events.Count == 0)
            {
                _logger.LogInformation("Extraction batch {BatchId} for session {SessionId} has no events left; skipping.", batchId, payload.SessionId);
                return;
            }

            memories = await extractor.ExtractFromEventsAsync(projectId, payload.SessionId, events, ct).ConfigureAwait(false);
            await sessionRepository.MarkExtractionBatchDoneAsync(batchId, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            await DeleteSummarizedEventsAsync(projectId, payload.SessionId, ct).ConfigureAwait(false);
            source = $"event batch {batchId} ({events.Count} events)";
        }
        else if (!string.IsNullOrWhiteSpace(payload.CompactSummary))
        {
            memories = await extractor.ExtractFromCompactSummaryAsync(projectId, payload.SessionId, payload.CompactSummary, job.CreatedAt, ct).ConfigureAwait(false);
            source = "compact summary";
        }
        else
        {
            throw new InvalidOperationException($"Job {job.Id}: {nameof(ExtractMemoryPayload)} names no source to extract from.");
        }

        _logger.LogInformation(
            "Memory extraction produced {MemoryCount} memories for session {SessionId} from {Source}.",
            memories.Count, payload.SessionId, source);

        await _activityNotifier.NotifyAsync(projectId, ProjectActivityKind.MemoryCreated, ct).ConfigureAwait(false);
    }

    // Summarization and extraction finish in either order; whichever runs second frees the events
    // (ISessionRepository.DeleteEventsUpToAsync only deletes extracted ones up to the last chunk).
    private async Task DeleteSummarizedEventsAsync(Guid projectId, Guid sessionId, CancellationToken ct)
    {
        if (settingsService is null)
        {
            return;
        }

        var settings = await settingsService.GetAsync(projectId, ct).ConfigureAwait(false);
        if (!settings.DeleteSessionEventsAfterSummary)
        {
            return;
        }

        var chunks = await sessionRepository.GetSummaryChunksAsync(projectId, sessionId, ct).ConfigureAwait(false);
        if (chunks.Count > 0)
        {
            await sessionRepository.DeleteEventsUpToAsync(sessionId, chunks[^1].ToEventAt, ct).ConfigureAwait(false);
        }
    }
}
