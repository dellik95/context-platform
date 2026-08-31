using Context.Domain.Entities;

namespace Context.Infrastructure.Abstractions;

public interface IEmbeddingRepository
{
    /// <summary>Looks up a cached embedding scoped to the given provider, so switching providers never returns another provider's stale vector for the same content hash.</summary>
    Task<Embedding?> GetByContentHashAsync(string contentHash, string provider, CancellationToken ct);
    Task<IReadOnlyDictionary<string, Embedding>> GetByContentHashesAsync(IEnumerable<string> contentHashes, string provider, CancellationToken ct);

    /// <summary>Returns the subset of the given content hashes that have no stored embedding yet for the given provider.</summary>
    Task<IReadOnlyList<string>> GetMissingContentHashesAsync(IEnumerable<string> contentHashes, string provider, CancellationToken ct);

    Task UpsertAsync(Embedding embedding, CancellationToken ct);
}
