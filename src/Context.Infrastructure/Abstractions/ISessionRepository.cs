using Context.Domain.Entities;

namespace Context.Infrastructure.Abstractions;

public interface ISessionRepository
{
    Task<Session?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Session> AddAsync(Session session, CancellationToken ct);
    Task UpdateAsync(Session session, CancellationToken ct);

    Task AddEventAsync(SessionEvent sessionEvent, CancellationToken ct);
    Task<IReadOnlyList<SessionEvent>> GetEventsAsync(Guid sessionId, CancellationToken ct);

    Task UpsertSummaryAsync(SessionSummary summary, CancellationToken ct);
    Task<SessionSummary?> GetSummaryAsync(Guid sessionId, CancellationToken ct);
}
