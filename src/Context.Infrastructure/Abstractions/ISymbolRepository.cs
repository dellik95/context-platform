using Context.Domain.Entities;

namespace Context.Infrastructure.Abstractions;

public interface ISymbolRepository
{
    Task<Symbol?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Symbol>> ListByFileAsync(Guid fileId, CancellationToken ct);
    Task<IReadOnlyList<Symbol>> SearchByNameAsync(Guid projectId, string namePattern, int limit, CancellationToken ct);

    /// <summary>Atomically replaces all symbols owned by a file — used on re-index of a changed file.</summary>
    Task ReplaceForFileAsync(Guid fileId, IReadOnlyList<Symbol> symbols, CancellationToken ct);
}
