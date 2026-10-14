using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Infrastructure.Repositories;

public class FileRepository(ContextDbContext db) : IFileRepository
{
    public Task<DomainFile?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id, ct);

    public Task<DomainFile?> GetByRelativePathAsync(Guid projectId, string relativePath, CancellationToken ct) =>
        db.Files.AsNoTracking()
            .FirstOrDefaultAsync(f => f.ProjectId == projectId && f.RelativePath == relativePath, ct);

    public async Task<IReadOnlyList<DomainFile>> ListByProjectAsync(Guid projectId, bool includeDeleted, CancellationToken ct)
    {
        var query = db.Files.AsNoTracking().Where(f => f.ProjectId == projectId);
        if (!includeDeleted)
        {
            query = query.Where(f => !f.IsDeleted);
        }

        return await query.OrderBy(f => f.RelativePath).ToListAsync(ct);
    }

    public async Task<DomainFile> UpsertAsync(DomainFile file, CancellationToken ct)
    {
        var existing = file.Id != Guid.Empty
            ? await db.Files.FirstOrDefaultAsync(f => f.Id == file.Id, ct)
            : await db.Files.FirstOrDefaultAsync(f => f.ProjectId == file.ProjectId && f.RelativePath == file.RelativePath, ct);

        if (existing is null)
        {
            if (file.Id == Guid.Empty)
            {
                file.Id = Guid.NewGuid();
            }

            db.Files.Add(file);
        }
        else
        {
            existing.Language = file.Language;
            existing.ContentHash = file.ContentHash;
            existing.SizeBytes = file.SizeBytes;
            existing.IsDeleted = file.IsDeleted;
            existing.UpdatedAt = file.UpdatedAt;

            // Summary is populated by a separate, later UpsertAsync call once IndexingPipeline has
            // run the file summarizer (it isn't known at the initial per-file upsert). A null here
            // means "not computed/updated this call", not "clear it" -- otherwise the initial
            // upsert of every reindex would wipe out a previously computed summary before the
            // pipeline gets a chance to recompute (or skip recomputing, e.g. offline LLM) it.
            if (file.Summary is not null)
            {
                existing.Summary = file.Summary;
            }

            file = existing;
        }

        await db.SaveChangesAsync(ct);
        return file;
    }

    public async Task SetContentHashAsync(Guid fileId, string contentHash, CancellationToken ct) =>
        await db.Files.Where(f => f.Id == fileId).ExecuteUpdateAsync(setters => setters.SetProperty(f => f.ContentHash, contentHash), ct);

    public async Task MarkDeletedAsync(Guid fileId, CancellationToken ct)
    {
        var file = await db.Files.FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null)
        {
            return;
        }

        file.IsDeleted = true;
        file.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
