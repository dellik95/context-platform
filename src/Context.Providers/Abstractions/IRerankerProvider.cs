namespace Context.Providers.Abstractions;

/// <summary>
/// Reorders a set of candidate texts by relevance to a query. Returns candidate indices,
/// best match first.
/// </summary>
public interface IRerankerProvider
{
    string Name { get; }

    /// <summary>Returns candidate indices into <paramref name="candidates"/>, best-first.</summary>
    Task<IReadOnlyList<int>> RerankAsync(string query, IReadOnlyList<string> candidates, CancellationToken ct);
}
