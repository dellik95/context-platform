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

    /// <summary>
    /// Persists <paramref name="merged"/> with its <paramref name="embedding"/> and supersedes
    /// <paramref name="supersededIds"/> in a single atomic unit.
    /// </summary>
    /// <remarks>
    /// Compaction's write, kept as one store operation rather than a <see cref="SaveAsync"/>
    /// followed by N <see cref="MarkSupersededAsync"/> calls: a crash between them publishes the
    /// merged memory while its sources are still active, so the same fact is retrieved twice and no
    /// later pass can repair it (the merge is flagged <c>IsCompacted</c> and skipped from then on).
    /// </remarks>
    Task SaveMergedAsync(Context.Domain.Entities.Memory merged, float[] embedding, IReadOnlyCollection<Guid> supersededIds, CancellationToken ct);

    /// <summary>
    /// Bumps <paramref name="memoryId"/>'s reinforcement count and timestamp, promoting it to
    /// <see cref="Domain.Enums.MemoryTier.LongTerm"/> once the new count reaches
    /// <paramref name="promotionReinforcementThreshold"/> (or the memory is pinned).
    /// </summary>
    /// <remarks>
    /// A single targeted update rather than <see cref="SaveAsync"/> with a mutated snapshot: two
    /// extractions reinforcing the same memory concurrently would otherwise each write back every
    /// column they read, so the second silently reverts the first's increment — and any Title/Body
    /// edit made in between.
    /// </remarks>
    /// <returns>The reinforcement count after the bump, or null when the memory no longer exists.</returns>
    Task<int?> ReinforceAsync(Guid memoryId, DateTimeOffset reinforcedAt, int promotionReinforcementThreshold, CancellationToken ct);
}
