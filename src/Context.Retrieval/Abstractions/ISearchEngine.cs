namespace Context.Retrieval.Abstractions;

/// <summary>
/// The single unified retrieval entry point — hybrid lexical + vector + graph search with
/// reranking. See <see cref="Context.Retrieval.HybridSearchEngine"/> for the default
/// implementation.
/// </summary>
public interface ISearchEngine
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken ct);
}
