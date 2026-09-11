using System.Text.Json;
using Context.Application.Memory.Triggers;
using Context.Application.Notifications;
using Context.Application.Settings;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;
using Context.Memory.Summarization;
using Microsoft.Extensions.Logging;

namespace Context.Application.Jobs.Handlers;

/// <summary>Summarizes a session and chains into <c>ExtractMemory</c>, per CONTRACTS.md §10 ("EndSessionAsync ... enqueues SummarizeSession -&gt; ExtractMemory").</summary>
public sealed class SummarizeSessionJobHandler(
    ISessionSummarizer summarizer,
    ISessionRepository sessionRepository,
    IJobQueue jobQueue,
    IProjectSettingsService settingsService,
    IProjectActivityNotifier? activityNotifier = null,
    IEnumerable<IMemoryExtractionTrigger>? extractionTriggers = null,
    ILogger<SummarizeSessionJobHandler>? logger = null) : IJobHandler
{
    // Mirrors production DI registration (ServiceCollectionExtensions.AddContextApplication) so a
    // caller that constructs this handler directly - e.g. existing unit tests predating this
    // trigger abstraction - gets the exact same two triggers, and therefore the exact same
    // behavior, without having to know about IMemoryExtractionTrigger at all.
    private static readonly IReadOnlyList<IMemoryExtractionTrigger> DefaultExtractionTriggers =
        [new EventThresholdExtractionTrigger(), new ConversationEndExtractionTrigger()];

    private readonly IProjectActivityNotifier _activityNotifier = activityNotifier ?? NullProjectActivityNotifier.Instance;
    private readonly IReadOnlyList<IMemoryExtractionTrigger> _extractionTriggers =
        extractionTriggers is null ? DefaultExtractionTriggers : [.. extractionTriggers];

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
        var settings = await settingsService.GetAsync(projectId, ct).ConfigureAwait(false);

        // since = the last recorded chunk's end, or the session's start when this is its first
        // chunk - either way, the boundary before which events are already covered by an
        // existing chunk and shouldn't be re-summarized.
        var existingChunks = await sessionRepository.GetSummaryChunksAsync(projectId, payload.SessionId, ct).ConfigureAwait(false);
        DateTimeOffset? since = existingChunks.Count > 0 ? existingChunks[^1].ToEventAt : session?.StartedAt;

        // Gate against ProjectSettingsDto.MinEventsForSessionSummary - SessionSummaryScheduler
        // already enforces this for its own periodic firing, but ContextEngine.EndSessionAsync
        // enqueues this job unconditionally regardless of how little (or how much) happened, so
        // without a check here every session - even one that ended after a single trivial event,
        // or with literally nothing new since its last chunk - still got a persisted SessionSummary
        // row (product feedback: "I do not need to write empty sessions"). Two cases:
        //  - no new events at all: summarizing would only ever produce a placeholder ("No activity
        //    recorded") chunk, and merging it in would clobber a real title already set by an
        //    earlier chunk (SessionSummaryMerger.Merge keeps the LAST non-blank title) - so bail
        //    unconditionally, even for a session that already has real chunks.
        //  - some new events, but this would be the session's first-ever chunk and it doesn't clear
        //    the "worth summarizing" bar: skip rather than persist a thin, near-empty summary. A
        //    session that already has a real chunk is allowed through below that bar, since this is
        //    just topping up an already-meaningful summary rather than creating one from scratch.
        // On session end (payload.IsSessionEnd) the bar is waived entirely: ConversationEndExtractionTrigger
        // fires unconditionally there since there won't be a later pass to pick this chunk up, but it
        // only ever gets the chance to fire if a chunk is actually produced - a short session (e.g. a
        // couple of tool calls) that never clears MinEventsForSessionSummary was silently losing both
        // its summary and its memory extraction. The "no new events at all" bail still applies even on
        // session end - there's genuinely nothing to summarize.
        var allEvents = await sessionRepository.GetEventsAsync(projectId, payload.SessionId, ct).ConfigureAwait(false);
        var newEventCount = since is null ? allEvents.Count : allEvents.Count(e => e.Timestamp > since.Value);

        if (newEventCount == 0 || (!payload.IsSessionEnd && existingChunks.Count == 0 && newEventCount < settings.MinEventsForSessionSummary))
        {
            return;
        }

        var newChunk = await summarizer.SummarizeAsync(projectId, payload.SessionId, since, ct).ConfigureAwait(false);
        var savedChunk = await sessionRepository.AddSummaryChunkAsync(newChunk, ct).ConfigureAwait(false);

        // Everything up to this chunk's own ToEventAt is now fully covered by it - ExtractMemory
        // (below) reads only the chunk, never raw events, so it's safe to drop them here rather than
        // let SessionEvents grow unbounded. Opt-in: the dashboard's session detail view and the MCP
        // recall tool still read raw events for history, so a project keeps them until it asks to
        // trade that away.
        if (settings.DeleteSessionEventsAfterSummary)
        {
            await sessionRepository.DeleteEventsUpToAsync(payload.SessionId, savedChunk.ToEventAt, ct).ConfigureAwait(false);
        }

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
            ? settings.MinEventsForMemoryExtraction
            : 0;

        // §E.1: each registered IMemoryExtractionTrigger independently justifies running
        // extraction for this chunk - fires once ANY of them returns true (today: event-threshold
        // OR session-end, the same two conditions the old inline `if` ORed together). The
        // no-session -> threshold-0 rule above is folded into the settings snapshot handed to the
        // triggers rather than special-cased here, so EventThresholdExtractionTrigger stays a pure
        // comparison against Settings.MinEventsForMemoryExtraction.
        var triggerContext = new MemoryExtractionTriggerContext(
            payload.IsSessionEnd,
            savedChunk.EventCount,
            settings with { MinEventsForMemoryExtraction = minEventsForExtraction });
        var firedTrigger = _extractionTriggers.FirstOrDefault(t => t.ShouldFire(triggerContext));

        if (firedTrigger is not null)
        {
            logger?.LogInformation(
                "Memory extraction triggered by {Trigger} for session {SessionId} ({EventCount} events).",
                firedTrigger.Name, payload.SessionId, savedChunk.EventCount);

            await jobQueue.EnqueueAsync(
                JobType.ExtractMemory,
                new ExtractMemoryPayload(payload.SessionId, savedChunk.Id),
                session?.ProjectId,
                ct: ct).ConfigureAwait(false);
        }
    }
}
