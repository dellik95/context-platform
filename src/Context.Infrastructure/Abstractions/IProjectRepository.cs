using Context.Domain.Entities;

namespace Context.Infrastructure.Abstractions;

public interface IProjectRepository
{
    Task<Project?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Project?> GetByRootPathAsync(string rootPath, CancellationToken ct);
    Task<IReadOnlyList<Project>> ListAsync(CancellationToken ct);
    Task<Project> AddAsync(Project project, CancellationToken ct);
    Task UpdateAsync(Project project, CancellationToken ct);
}
