using System.Text.Json;
using Context.Application.Notifications;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Context.Memory.Summarization;

namespace Context.Application.Jobs.Handlers;

/// <summary>Summarizes a session and chains into <c>ExtractMemory</c>, per CONTRACTS.md §10 ("EndSessionAsync ... enqueues SummarizeSession -&gt; ExtractMemory").</summary>
public sealed class SummarizeSessionJobHandler(
    ISessionSummarizer summarizer,
    ISessionRepository sessionRepository,
    IJobQueue jobQueue,
    IProjectSettingsService settingsService,
    IProjectActivityNotifier? activityNotifier = null) : IJobHandler
{
    private readonly IProjectActivityNotifier _activityNotifier = activityNotifier ?? NullProjectActivityNotifier.Instance;

    public JobType Type => JobType.SummarizeSession;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<SummarizeSessionPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(SummarizeSessionPayload)}.");

        // SummarizeSession jobs are always enqueued with the owning session's ProjectId (see
        // ContextEngine.EndSessionAsync / SessionSummaryScheduler) - required here to scope the
        // now project-checked ISessionRepository lookups below.
        var projectId = job.ProjectId
            ?? throw new InvalidOperationException($"Job {job.Id}: {nameof(JobType)}.{nameof(JobType.SummarizeSession)} jobs must carry a ProjectId.");

        var session = await sessionRepository.GetByIdAsync(projectId, payload.SessionId, ct).ConfigureAwait(false);

        // since = the last recorded chunk's end, or the session's start when this is its first
        // chunk - either way, the boundary before which events are already covered by an
        // existing chunk and shouldn't be re-summarized.
        var existingChunks = await sessionRepository.GetSummaryChunksAsync(projectId, payload.SessionId, ct).ConfigureAwait(false);
        DateTimeOffset? since = existingChunks.Count > 0 ? existingChunks[^1].ToEventAt : session?.StartedAt;

        var newChunk = await summarizer.SummarizeAsync(projectId, payload.SessionId, since, ct).ConfigureAwait(false);
        var savedChunk = await sessionRepository.AddSummaryChunkAsync(newChunk, ct).ConfigureAwait(false);

        var allChunks = await sessionRepository.GetSummaryChunksAsync(projectId, payload.SessionId, ct).ConfigureAwait(false);
        var mergedSummary = SessionSummaryMerger.Merge(payload.SessionId, allChunks);
        await sessionRepository.UpsertSummaryAsync(mergedSummary, ct).ConfigureAwait(false);

        // This also runs for a still-Active session (SessionSummaryScheduler's periodic mid-session
        // summary, not just EndSessionAsync's final one) - the Dashboard's Sessions panel wants to
        // know either way, so it can refresh a session's detail without the user re-selecting it.
        if (session is not null)
        {
            await _activityNotifier.NotifyAsync(session.ProjectId, ProjectActivityKind.SessionSummaryUpdated, ct).ConfigureAwait(false);
        }

        // ExtractMemory does real work per chunk (LLM candidate generation, embedding, dedup) -
        // unlike summarization itself, a frequent periodic chunk with only a handful of new events
        // isn't worth that unless the session is actually ending, at which point every chunk must
        // be processed since there won't be a later pass to pick it up.
        var minEventsForExtraction = session is not null
            ? (await settingsService.GetAsync(session.ProjectId, ct).ConfigureAwait(false)).MinEventsForMemoryExtraction
            : 0;

        if (payload.IsSessionEnd || savedChunk.EventCount >= minEventsForExtraction)
        {
            await jobQueue.EnqueueAsync(
                JobType.ExtractMemory,
                new ExtractMemoryPayload(payload.SessionId, savedChunk.Id),
                session?.ProjectId,
                ct: ct).ConfigureAwait(false);
        }
    }
}
