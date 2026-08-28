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
    Task<IReadOnlyList<SessionEvent>> GetEventsAsync(Guid sessionId, CancellationToken ct);

    /// <summary>
    /// Resolves the owning project for a session. <see cref="SessionSummary"/> (CONTRACTS.md §1)
    /// only carries a SessionId, but memory extraction needs the ProjectId to scope
    /// deduplication and to stamp the new <see cref="Memory"/> rows — hence this addition to
    /// the ISessionEventSource seam beyond the single GetEventsAsync member described in the
    /// task brief. See final report for rationale.
    /// </summary>
    Task<Guid> GetProjectIdAsync(Guid sessionId, CancellationToken ct);
}
