using Context.Domain.Entities;

namespace Context.Memory.Abstractions;

/// <summary>
/// Data-access seam for session-scoped data, implemented against the real repository by
/// Context.Application. Kept minimal on purpose so Context.Memory has no dependency on
/// Context.Infrastructure/EF Core.
/// </summary>
public interface ISessionEventSource
{
    /// <summary>All recorded events for the session, in chronological order.</summary>
    Task<IReadOnlyList<SessionEvent>> GetEventsAsync(Guid projectId, Guid sessionId, CancellationToken ct);
}
