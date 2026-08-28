using Context.Domain.Entities;

namespace Context.Infrastructure.Abstractions;

public interface IMemoryRepository
{
    Task<Memory?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Memory> AddAsync(Memory memory, CancellationToken ct);
    Task UpdateAsync(Memory memory, CancellationToken ct);

    /// <summary>Simple substring match over Title/Body scoped to a project — the offline-friendly
    /// fallback path; a real implementation can layer embedding similarity on top via
    /// Context.Retrieval without changing this contract.</summary>
    Task<IReadOnlyList<Memory>> SearchAsync(Guid projectId, string query, bool includeSuperseded, CancellationToken ct);

    Task<IReadOnlyList<Memory>> ListByProjectAsync(Guid projectId, bool includeSuperseded, CancellationToken ct);
}
