using Context.Domain.Entities;

namespace Context.Infrastructure.Abstractions;

public interface ISymbolRepository
{
    Task<Symbol?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Symbol>> ListByFileAsync(Guid fileId, CancellationToken ct);
    Task<IReadOnlyList<Symbol>> SearchByNameAsync(Guid projectId, string namePattern, int limit, CancellationToken ct);

    /// <summary>Every distinct symbol name in the project, across all files -- for filtering a call-graph candidate name against "is this a real project symbol at all".</summary>
    Task<IReadOnlySet<string>> GetAllNamesAsync(Guid projectId, CancellationToken ct);

    /// <summary>Total symbol count across every non-deleted file in the project, in one aggregate query -- avoids an O(files) per-file ListByFileAsync loop just to sum counts.</summary>
    Task<int> CountByProjectAsync(Guid projectId, CancellationToken ct);

    /// <summary>Atomically replaces all symbols owned by a file — used on re-index of a changed file.</summary>
    Task ReplaceForFileAsync(Guid fileId, IReadOnlyList<Symbol> symbols, CancellationToken ct);
}
