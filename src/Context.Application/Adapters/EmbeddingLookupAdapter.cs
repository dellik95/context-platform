using Context.Providers.Abstractions;
using Context.Retrieval.Abstractions;
using Microsoft.Extensions.AI;

namespace Context.Application.Adapters;

public sealed class EmbeddingLookupAdapter(IProviderResolver providerResolver) : IEmbeddingLookup
{
    public async Task<float[]> EmbedAsync(Guid projectId, string text, CancellationToken ct)
    {
        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
        var result = await embeddingGenerator.GenerateAsync(text, cancellationToken: ct).ConfigureAwait(false);
        return result.Vector.ToArray();
    }

    public async Task<float[][]> EmbedBatchAsync(Guid projectId, IReadOnlyList<string> texts, CancellationToken ct)
    {
        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
        var result = await embeddingGenerator.GenerateAsync(texts, cancellationToken: ct).ConfigureAwait(false);
        return [.. result.Select(e => e.Vector.ToArray())];
    }
}
