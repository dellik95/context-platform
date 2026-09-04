using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;

namespace Context.Application.Adapters;

/// <summary>Adapts Context.Memory's <see cref="ISessionEventSource"/> seam onto <see cref="ISessionRepository"/>.</summary>
public sealed class SessionEventSourceAdapter(ISessionRepository sessionRepository) : ISessionEventSource
{
    public Task<IReadOnlyList<SessionEvent>> GetEventsAsync(Guid projectId, Guid sessionId, CancellationToken ct) =>
        sessionRepository.GetEventsAsync(projectId, sessionId, ct);
}
