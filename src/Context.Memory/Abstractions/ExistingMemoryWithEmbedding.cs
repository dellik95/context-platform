using Context.Domain.Entities;

namespace Context.Memory.Abstractions;

/// <summary>
/// An existing memory paired with its stored embedding vector, as returned by
/// <see cref="IMemoryStore.GetExistingWithEmbeddingsAsync"/> for similarity comparison
/// during extraction.
/// </summary>
public record ExistingMemoryWithEmbedding(Context.Domain.Entities.Memory Memory, float[] Embedding);
