using DomainFile = Context.Domain.Entities.File;

namespace Context.Infrastructure.Abstractions;

public interface IFileRepository
{
    Task<DomainFile?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<DomainFile?> GetByRelativePathAsync(Guid projectId, string relativePath, CancellationToken ct);
    Task<IReadOnlyList<DomainFile>> ListByProjectAsync(Guid projectId, bool includeDeleted, CancellationToken ct);

    /// <summary>Inserts the file if new, or updates it in place (matched by Id) if it already exists.</summary>
    Task<DomainFile> UpsertAsync(DomainFile file, CancellationToken ct);

    Task MarkDeletedAsync(Guid fileId, CancellationToken ct);

    /// <summary>Records <paramref name="contentHash"/> as the content the file's index now reflects. Written as the
    /// last step of indexing: while a file's hash is empty its index is incomplete, so a retry redoes everything.</summary>
    Task SetContentHashAsync(Guid fileId, string contentHash, CancellationToken ct);
}
