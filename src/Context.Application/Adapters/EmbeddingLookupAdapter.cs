using Context.Providers.Abstractions;
using Context.Retrieval.Abstractions;

namespace Context.Application.Adapters;

/// <summary>Adapts the real <see cref="IEmbeddingProvider"/> onto Context.Retrieval's local seam.</summary>
public sealed class EmbeddingLookupAdapter(IEmbeddingProvider embeddingProvider) : IEmbeddingLookup
{
    public Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
        embeddingProvider.EmbedAsync(text, ct);

    public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
        embeddingProvider.EmbedBatchAsync(texts, ct);
}
