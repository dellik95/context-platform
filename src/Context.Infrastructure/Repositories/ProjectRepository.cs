using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Repositories;

public class ProjectRepository(ContextDbContext db) : IProjectRepository
{
    public Task<Project?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);

    public Task<Project?> GetByRootPathAsync(string rootPath, CancellationToken ct) =>
        db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.RootPath == rootPath, ct);

    public Task<Project?> GetByBearerTokenHashAsync(string tokenHash, CancellationToken ct) =>
        db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.BearerTokenHash == tokenHash, ct);

    public async Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct) =>
        await db.Projects.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);

    public async Task<Project> AddAsync(Project project, CancellationToken ct)
    {
        if (project.Id == Guid.Empty)
        {
            project.Id = Guid.NewGuid();
        }

        db.Projects.Add(project);
        await db.SaveChangesAsync(ct);
        return project;
    }

    public async Task UpdateAsync(Project project, CancellationToken ct)
    {
        db.Projects.Update(project);
        await db.SaveChangesAsync(ct);
    }
}
