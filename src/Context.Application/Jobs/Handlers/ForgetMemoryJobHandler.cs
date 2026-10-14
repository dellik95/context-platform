using System.Text.Json;
using Context.Application.Notifications;
using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Application.Jobs.Handlers;

/// <summary>Deletes a memory the caller was told is being deleted. A memory that is already gone is a
/// success, so a retry after a half-applied attempt finishes cleanly.</summary>
public sealed class ForgetMemoryJobHandler(
    IContextEngine engine,
    IProjectActivityNotifier? activityNotifier = null) : IJobHandler
{
    private readonly IProjectActivityNotifier _activityNotifier = activityNotifier ?? NullProjectActivityNotifier.Instance;

    public JobType Type => JobType.ForgetMemory;

    public async Task HandleAsync(Job job, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ForgetMemoryPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException($"Job {job.Id}: could not deserialize {nameof(ForgetMemoryPayload)}.");

        var projectId = job.ProjectId
            ?? throw new InvalidOperationException($"Job {job.Id}: {nameof(JobType)}.{nameof(JobType.ForgetMemory)} jobs must carry a ProjectId.");

        await engine.ForgetMemoryAsync(projectId, payload.MemoryId, ct).ConfigureAwait(false);
        await _activityNotifier.NotifyAsync(projectId, ProjectActivityKind.MemoryChanged, ct).ConfigureAwait(false);
    }
}
