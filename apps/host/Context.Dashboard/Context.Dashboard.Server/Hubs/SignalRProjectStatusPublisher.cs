using Context.Application.Indexing;
using Context.Dashboard.Server.Api;

namespace Context.Dashboard.Server.Hubs;

/// <summary>SignalR-backed <see cref="IProjectStatusPublisher"/>: pushes the project's refreshed status DTO to its hub group.
/// Scoped, so the status is read through the same scope as the job that finished the reindex.</summary>
public sealed class SignalRProjectStatusPublisher(IServiceProvider services, IndexingHubNotifier hubNotifier) : IProjectStatusPublisher
{
    public async Task PublishAsync(Guid projectId, CancellationToken ct)
    {
        var status = await ProjectEndpoints.BuildStatusDtoAsync(projectId, services, ct).ConfigureAwait(false);
        if (status is not null)
        {
            await hubNotifier.ProjectStatusChangedAsync(projectId, status, ct).ConfigureAwait(false);
        }
    }
}
