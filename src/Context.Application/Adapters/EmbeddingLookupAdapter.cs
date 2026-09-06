using Context.Providers.Abstractions;
using Context.Providers.Embeddings;
using Context.Retrieval.Abstractions;
using Microsoft.Extensions.AI;

namespace Context.Application.Adapters;

public sealed class EmbeddingLookupAdapter(IProviderResolver providerResolver) : IEmbeddingLookup
{
    // Flags this as a live user query to OllamaEmbeddingGeneratorDecorator, which lets it skip the
    // concurrency gate it applies to background EmbedChunk jobs (see InteractiveRequestKey's own
    // remarks) -- HybridSearchEngine's only embedding call site is this one, so a search should
    // never queue up behind a large indexing backlog it has nothing to do with.
    private static readonly EmbeddingGenerationOptions InteractiveOptions = new()
    {
        AdditionalProperties = new AdditionalPropertiesDictionary { [OllamaEmbeddingGeneratorDecorator.InteractiveRequestKey] = true }
    };

    public async Task<float[]> EmbedAsync(Guid projectId, string text, CancellationToken ct)
    {
        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
        var result = await embeddingGenerator.GenerateAsync(text, InteractiveOptions, ct).ConfigureAwait(false);
        return result.Vector.ToArray();
    }

    public async Task<float[][]> EmbedBatchAsync(Guid projectId, IReadOnlyList<string> texts, CancellationToken ct)
    {
        var embeddingGenerator = await providerResolver.GetEmbeddingGeneratorAsync(projectId, ct).ConfigureAwait(false);
        var result = await embeddingGenerator.GenerateAsync(texts, cancellationToken: ct).ConfigureAwait(false);
        return [.. result.Select(e => e.Vector.ToArray())];
    }
}
