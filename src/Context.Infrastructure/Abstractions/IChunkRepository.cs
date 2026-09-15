using Context.Domain.Entities;

namespace Context.Infrastructure.Abstractions;

public interface IChunkRepository
{
    Task<Chunk?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Chunk>> ListByFileAsync(Guid fileId, CancellationToken ct);
    Task<IReadOnlyList<Chunk>> GetByContentHashesAsync(IEnumerable<string> contentHashes, CancellationToken ct);

    /// <summary>Every chunk owned by any non-deleted file in the project — used by
    /// <c>RecreateEmbeddingsJobHandler</c>, which has no other "all chunks for a project" query to
    /// build on since everything else here is scoped per-file.</summary>
    Task<IReadOnlyList<Chunk>> ListByProjectAsync(Guid projectId, CancellationToken ct);

    /// <summary>Atomically replaces all chunks owned by a file — used on re-index of a changed file.</summary>
    Task ReplaceForFileAsync(Guid fileId, IReadOnlyList<Chunk> chunks, CancellationToken ct);
}
