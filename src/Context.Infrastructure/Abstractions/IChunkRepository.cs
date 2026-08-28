using Context.Domain.Entities;

namespace Context.Infrastructure.Abstractions;

public interface IChunkRepository
{
    Task<Chunk?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Chunk>> ListByFileAsync(Guid fileId, CancellationToken ct);
    Task<IReadOnlyList<Chunk>> GetByContentHashesAsync(IEnumerable<string> contentHashes, CancellationToken ct);

    /// <summary>Atomically replaces all chunks owned by a file — used on re-index of a changed file.</summary>
    Task ReplaceForFileAsync(Guid fileId, IReadOnlyList<Chunk> chunks, CancellationToken ct);
}
