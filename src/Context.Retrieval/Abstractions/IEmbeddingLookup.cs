namespace Context.Retrieval.Abstractions;

/// <summary>
/// Text-in, vector-out seam. Context.Retrieval does not reference Context.Providers/
/// Microsoft.Extensions.AI (see CONTRACTS.md dependency graph); Context.Application adapts the
/// real <c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c> to this interface at
/// composition time.
/// </summary>
public interface IEmbeddingLookup
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct);

    Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
