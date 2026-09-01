using Context.Retrieval.Abstractions;
using Microsoft.Extensions.AI;

namespace Context.Application.Adapters;

public sealed class EmbeddingLookupAdapter(IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator) : IEmbeddingLookup
{
    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var result = await embeddingGenerator.GenerateAsync(text, cancellationToken: ct).ConfigureAwait(false);
        return result.Vector.ToArray();
    }

    public async Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var result = await embeddingGenerator.GenerateAsync(texts, cancellationToken: ct).ConfigureAwait(false);
        return [.. result.Select(e => e.Vector.ToArray())];
    }
}
