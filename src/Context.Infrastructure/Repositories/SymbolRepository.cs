using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Repositories;

public class SymbolRepository(ContextDbContext db) : ISymbolRepository
{
    public Task<Symbol?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Symbols.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<Symbol>> ListByFileAsync(Guid fileId, CancellationToken ct) =>
        await db.Symbols.AsNoTracking()
            .Where(s => s.FileId == fileId)
            .OrderBy(s => s.StartLine)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Symbol>> SearchByNameAsync(Guid projectId, string namePattern, int limit, CancellationToken ct)
    {
        var pattern = $"%{namePattern}%";
        var matches = await (
            from s in db.Symbols.AsNoTracking()
            join f in db.Files.AsNoTracking() on s.FileId equals f.Id
            where f.ProjectId == projectId && !f.IsDeleted && EF.Functions.Like(s.QualifiedName, pattern)
            select s
        ).ToListAsync(ct);

        // Ordered by match quality, not alphabetically (SQLite's EF Core provider can't translate
        // this ranking, so it runs client-side): an exact/prefix/substring hit on the symbol's own short Name ranks above one
        // that only matched somewhere in QualifiedName's namespace/parent prefix, so searching
        // "AuthMiddleware" surfaces the class itself before an unrelated symbol that merely lives in
        // a namespace containing "Auth".
        return [.. matches
            .OrderBy(s => MatchRank(s.Name, namePattern))
            .ThenBy(s => s.QualifiedName.Length)
            .ThenBy(s => s.QualifiedName, StringComparer.Ordinal)
            .Take(limit)];
    }

    private static int MatchRank(string name, string namePattern)
    {
        if (string.Equals(name, namePattern, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith(namePattern, StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains(namePattern, StringComparison.OrdinalIgnoreCase)) return 2;
        return 3; // matched only via QualifiedName's namespace/parent prefix
    }

    public async Task<IReadOnlySet<string>> GetAllNamesAsync(Guid projectId, CancellationToken ct)
    {
        var names = await (
            from s in db.Symbols.AsNoTracking()
            join f in db.Files.AsNoTracking() on s.FileId equals f.Id
            where f.ProjectId == projectId && !f.IsDeleted
            select s.Name
        ).Distinct().ToListAsync(ct);

        return names.ToHashSet(StringComparer.Ordinal);
    }

    public Task<int> CountByProjectAsync(Guid projectId, CancellationToken ct) =>
        (
            from s in db.Symbols.AsNoTracking()
            join f in db.Files.AsNoTracking() on s.FileId equals f.Id
            where f.ProjectId == projectId && !f.IsDeleted
            select s.Id
        ).CountAsync(ct);

    public async Task ReplaceForFileAsync(Guid fileId, IReadOnlyList<Symbol> symbols, CancellationToken ct)
    {
        await using var tx = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct)
            : null;

        var existing = await db.Symbols.Where(s => s.FileId == fileId).ToListAsync(ct);
        db.Symbols.RemoveRange(existing);

        foreach (var symbol in symbols)
        {
            if (symbol.Id == Guid.Empty)
            {
                symbol.Id = Guid.NewGuid();
            }

            symbol.FileId = fileId;
        }

        await db.Symbols.AddRangeAsync(symbols, ct);
        await db.SaveChangesAsync(ct);
        if (tx is not null)
        {
            await tx.CommitAsync(ct);
        }
    }
}
