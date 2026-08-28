using Context.Providers.Abstractions;
using Context.Retrieval.Abstractions;

namespace Context.Application.Adapters;

/// <summary>Adapts the real <see cref="IRerankerProvider"/> onto Context.Retrieval's local seam.</summary>
public sealed class RerankerAdapter(IRerankerProvider rerankerProvider) : IReranker
{
    public Task<IReadOnlyList<int>> RerankAsync(string query, IReadOnlyList<string> candidates, CancellationToken ct) =>
        rerankerProvider.RerankAsync(query, candidates, ct);
}
