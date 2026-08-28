using Context.Domain.Entities;

namespace Context.Memory.Abstractions;

/// <summary>
/// Data-access seam for reading/writing memories, implemented against the real repository
/// by Context.Application. Kept minimal on purpose so Context.Memory has no dependency on
/// Context.Infrastructure/EF Core.
/// </summary>
public interface IMemoryStore
{
    /// <summary>
    /// Existing memories for the project, together with their embeddings, used as the
    /// deduplication candidate set. Implementations should normally return only
    /// currently-active (non-superseded) memories, since a candidate is compared against
    /// the current state of project knowledge.
    /// </summary>
    Task<IReadOnlyList<ExistingMemoryWithEmbedding>> GetExistingWithEmbeddingsAsync(Guid projectId, CancellationToken ct);

    /// <summary>
    /// Upserts <paramref name="memory"/>. Pass a non-null <paramref name="embedding"/> when
    /// persisting a brand-new memory (the store also writes the corresponding
    /// <see cref="Domain.Entities.MemoryEmbedding"/> row); pass null when only updating fields
    /// on an already-stored memory (e.g. bumping ReinforcementCount/LastReinforcedAt/Tier
    /// during reinforcement), since its embedding is unchanged.
    /// </summary>
    Task SaveAsync(Context.Domain.Entities.Memory memory, float[]? embedding, CancellationToken ct);

    /// <summary>Marks <paramref name="oldMemoryId"/> as superseded by <paramref name="newMemoryId"/>. The old memory is not deleted.</summary>
    Task MarkSupersededAsync(Guid oldMemoryId, Guid newMemoryId, CancellationToken ct);
}
