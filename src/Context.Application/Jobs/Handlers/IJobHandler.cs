using Context.Domain.Entities;
using Context.Domain.Enums;

namespace Context.Application.Jobs.Handlers;

/// <summary>
/// One entry in <see cref="JobDispatcher"/>'s internal handler map. Implementations are resolved
/// per-job from a fresh DI scope (see <see cref="JobDispatcher"/>), so constructor dependencies
/// may freely be Scoped (repositories) or Singleton (providers, graph/queue services).
/// </summary>
public interface IJobHandler
{
    JobType Type { get; }

    Task HandleAsync(Job job, CancellationToken ct);
}
