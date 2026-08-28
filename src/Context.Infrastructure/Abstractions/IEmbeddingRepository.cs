using Context.Domain.Entities;

namespace Context.Infrastructure.Abstractions;

public interface IEmbeddingRepository
{
    Task<Embedding?> GetByContentHashAsync(string contentHash, CancellationToken ct);
    Task<IReadOnlyDictionary<string, Embedding>> GetByContentHashesAsync(IEnumerable<string> contentHashes, CancellationToken ct);

    /// <summary>Returns the subset of the given content hashes that have no stored embedding yet.</summary>
    Task<IReadOnlyList<string>> GetMissingContentHashesAsync(IEnumerable<string> contentHashes, CancellationToken ct);

    Task UpsertAsync(Embedding embedding, CancellationToken ct);
}
