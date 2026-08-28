using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;

namespace Context.Application.Adapters;

/// <summary>Adapts Context.Memory's <see cref="ISessionEventSource"/> seam onto <see cref="ISessionRepository"/>.</summary>
public sealed class SessionEventSourceAdapter(ISessionRepository sessionRepository) : ISessionEventSource
{
    public Task<IReadOnlyList<SessionEvent>> GetEventsAsync(Guid sessionId, CancellationToken ct) =>
        sessionRepository.GetEventsAsync(sessionId, ct);

    public async Task<Guid> GetProjectIdAsync(Guid sessionId, CancellationToken ct)
    {
        var session = await sessionRepository.GetByIdAsync(sessionId, ct).ConfigureAwait(false);
        return session?.ProjectId
            ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");
    }
}
