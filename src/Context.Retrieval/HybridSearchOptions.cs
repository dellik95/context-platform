namespace Context.Retrieval;

/// <summary>Tunable parameters for <see cref="HybridSearchEngine"/>.</summary>
public sealed class HybridSearchOptions
{
    /// <summary>How many of the top merged lexical/vector candidates seed graph expansion.</summary>
    public int GraphExpansionSeedCount { get; init; } = HybridSearchEngine.DefaultGraphExpansionSeedCount;

    /// <summary>Hop count passed to <see cref="Context.Graph.Abstractions.IGraphTraversal.NeighborsAsync"/> per seed.</summary>
    public int GraphExpansionHops { get; init; } = HybridSearchEngine.DefaultGraphExpansionHops;

    /// <summary>How many top-scored candidates are handed to the reranker.</summary>
    public int RerankTopK { get; init; } = HybridSearchEngine.DefaultRerankTopK;
}
