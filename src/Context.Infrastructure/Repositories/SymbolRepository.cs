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
        return await (
            from s in db.Symbols.AsNoTracking()
            join f in db.Files.AsNoTracking() on s.FileId equals f.Id
            where f.ProjectId == projectId && !f.IsDeleted && EF.Functions.Like(s.QualifiedName, pattern)
            orderby s.QualifiedName
            select s
        ).Take(limit).ToListAsync(ct);
    }

    public async Task ReplaceForFileAsync(Guid fileId, IReadOnlyList<Symbol> symbols, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

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
        await tx.CommitAsync(ct);
    }
}
