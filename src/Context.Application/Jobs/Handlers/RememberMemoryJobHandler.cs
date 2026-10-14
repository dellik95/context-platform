using System.Text.Json;
using Context.Application.Notifications;
using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Application.Jobs.Handlers;

/// <summary>Stores a memory the caller was already told is "saving": embeds it, judges it against its
/// neighbours and consolidates it. A provider outage fails the job, and the retry budget outlasts it.</summary>
public sealed class RememberMemoryJobHandler(
    IContextEngine engine,
    IProjectActivityNotifier? activityNotifier = null) : IJobHandler
{
    private readonly IProjectActivityNotifier _activityNotifier = activityNotifier ?? NullProjectActivityNotifier.Instance;

    public JobType Type => JobType.RememberMemory;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<RememberMemoryPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(RememberMemoryPayload)}.");

        var projectId = job.ProjectId
            ?? throw new InvalidOperationException($"Job {job.Id}: {nameof(JobType)}.{nameof(JobType.RememberMemory)} jobs must carry a ProjectId.");

        await engine.RememberExplicitlyAsync(
            projectId, payload.Content, payload.Type, ct,
            payload.Title, payload.Topic, payload.Facts, payload.Concepts, payload.FilesRead, payload.FilesModified).ConfigureAwait(false);

        await _activityNotifier.NotifyAsync(projectId, ProjectActivityKind.MemoryChanged, ct).ConfigureAwait(false);
    }
}
