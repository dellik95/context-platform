namespace Context.Retrieval.Abstractions;

/// <summary>
/// Local mirror of <c>Context.Providers.Abstractions.IEmbeddingProvider</c>'s method shape.
/// Context.Retrieval does not reference Context.Providers (see CONTRACTS.md dependency graph);
/// Context.Application adapts the real provider implementation to this interface at composition
/// time. Named distinctly from <c>IEmbeddingProvider</c> to avoid confusion between the two
/// projects' types.
/// </summary>
public interface IEmbeddingLookup
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct);

    Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
