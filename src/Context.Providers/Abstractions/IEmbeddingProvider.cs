namespace Context.Providers.Abstractions;

/// <summary>
/// Produces vector embeddings for text. Implementations range from a fully offline,
/// zero-dependency pseudo-embedding (<see cref="Context.Providers.Embeddings.NoOpEmbeddingProvider"/>)
/// to real remote providers (OpenAI, Ollama).
/// </summary>
public interface IEmbeddingProvider
{
    string Name { get; }

    int Dimensions { get; }

    Task<float[]> EmbedAsync(string text, CancellationToken ct);

    Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
