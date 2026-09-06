using Context.Domain.Entities;

namespace Context.Infrastructure.Abstractions;

public interface IMemoryRepository
{
    Task<Memory?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Memory> AddAsync(Memory memory, CancellationToken ct);
    Task UpdateAsync(Memory memory, CancellationToken ct);

    /// <summary>Hard delete — a user explicitly discarding a memory, not a supersession
    /// (superseded memories are kept for history via <see cref="Memory.SupersededById"/>).
    /// No-op (does not throw) if the id doesn't exist.</summary>
    Task DeleteAsync(Guid id, CancellationToken ct);

    /// <summary>Simple substring match over Title/Body scoped to a project — the offline-friendly
    /// fallback path; a real implementation can layer embedding similarity on top via
    /// Context.Retrieval without changing this contract. When <paramref name="topic"/> is
    /// non-null/whitespace, results are additionally restricted to memories whose
    /// <see cref="Memory.Topic"/> matches it (case-insensitive, exact match) — lets a caller
    /// combine free-text search with "only this topic" the way the dashboard's filter bar does.</summary>
    Task<IReadOnlyList<Memory>> SearchAsync(Guid projectId, string query, bool includeSuperseded, CancellationToken ct, string? topic = null);

    Task<IReadOnlyList<Memory>> ListByProjectAsync(Guid projectId, bool includeSuperseded, CancellationToken ct);
}
