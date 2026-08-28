using Context.Providers.Abstractions;

namespace Context.Providers.Reranking;

/// <summary>Identity reranker — returns candidates in their original order, unchanged.</summary>
public sealed class NoOpReranker : IRerankerProvider
{
    public string Name => "noop";

    public Task<IReadOnlyList<int>> RerankAsync(string query, IReadOnlyList<string> candidates, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<int> indices = [.. Enumerable.Range(0, candidates.Count)];
        return Task.FromResult(indices);
    }
}
