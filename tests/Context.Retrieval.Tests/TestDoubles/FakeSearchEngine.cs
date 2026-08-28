using Context.Retrieval.Abstractions;

namespace Context.Retrieval.Tests.TestDoubles;

/// <summary>
/// Minimal <see cref="ISearchEngine"/> test double that returns a fixed, caller-supplied list of
/// hits regardless of the query, so <see cref="ContextPackBuilder"/> tests can control exactly
/// what the knapsack/signature logic has to work with.
/// </summary>
internal sealed class FakeSearchEngine : ISearchEngine
{
    private readonly IReadOnlyList<SearchHit> _hits;

    public FakeSearchEngine(IReadOnlyList<SearchHit> hits) => _hits = hits;

    public SearchQuery? LastQuery { get; private set; }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        LastQuery = query;
        return Task.FromResult(_hits);
    }
}
