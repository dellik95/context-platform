using System.Text.Json;
using Context.Application.Notifications;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;

namespace Context.Application.Jobs.Handlers;

/// <summary>Summarizes a session and chains into <c>ExtractMemory</c>, per CONTRACTS.md §10 ("EndSessionAsync ... enqueues SummarizeSession -&gt; ExtractMemory").</summary>
public sealed class SummarizeSessionJobHandler(
    ISessionSummarizer summarizer,
    ISessionRepository sessionRepository,
    IJobQueue jobQueue,
    IProjectActivityNotifier? activityNotifier = null) : IJobHandler
{
    private readonly IProjectActivityNotifier _activityNotifier = activityNotifier ?? NullProjectActivityNotifier.Instance;

    public JobType Type => JobType.SummarizeSession;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<SummarizeSessionPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(SummarizeSessionPayload)}.");

        var summary = await summarizer.SummarizeAsync(payload.SessionId, ct).ConfigureAwait(false);
        await sessionRepository.UpsertSummaryAsync(summary, ct).ConfigureAwait(false);

        // This also runs for a still-Active session (SessionSummaryScheduler's periodic mid-session
        // summary, not just EndSessionAsync's final one) - the Dashboard's Sessions panel wants to
        // know either way, so it can refresh a session's detail without the user re-selecting it.
        var session = await sessionRepository.GetByIdAsync(payload.SessionId, ct).ConfigureAwait(false);
        if (session is not null)
        {
            await _activityNotifier.NotifyAsync(session.ProjectId, ProjectActivityKind.SessionSummaryUpdated, ct).ConfigureAwait(false);
        }

        await jobQueue.EnqueueAsync(JobType.ExtractMemory, new ExtractMemoryPayload(payload.SessionId), ct: ct).ConfigureAwait(false);
    }
}
