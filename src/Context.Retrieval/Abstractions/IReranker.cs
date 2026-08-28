namespace Context.Retrieval.Abstractions;

/// <summary>
/// Local mirror of <c>Context.Providers.Abstractions.IRerankerProvider</c>'s method shape.
/// Context.Retrieval does not reference Context.Providers (see CONTRACTS.md dependency graph);
/// Context.Application adapts the real provider implementation to this interface at composition
/// time. Named distinctly from <c>IRerankerProvider</c> to avoid confusion between the two
/// projects' types.
/// </summary>
public interface IReranker
{
    /// <summary>Returns candidate indices into <paramref name="candidates"/>, best-first.</summary>
    Task<IReadOnlyList<int>> RerankAsync(string query, IReadOnlyList<string> candidates, CancellationToken ct);
}
