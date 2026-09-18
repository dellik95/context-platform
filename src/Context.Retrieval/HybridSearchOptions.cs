namespace Context.Retrieval;

/// <summary>Tunable parameters for <see cref="HybridSearchEngine"/>.</summary>
public sealed class HybridSearchOptions
{
    /// <summary>How many of the top merged lexical/vector candidates seed graph expansion.</summary>
    public int GraphExpansionSeedCount { get; set; } = HybridSearchEngine.DefaultGraphExpansionSeedCount;

    /// <summary>Hop count passed to <see cref="Context.Graph.Abstractions.IGraphTraversal.NeighborsAsync"/> per seed.</summary>
    public int GraphExpansionHops { get; set; } = HybridSearchEngine.DefaultGraphExpansionHops;

    /// <summary>How many top-scored candidates are handed to the reranker.</summary>
    public int RerankTopK { get; set; } = HybridSearchEngine.DefaultRerankTopK;

    public bool SemanticBoostEnabled { get; set; } = true;

    public double SemanticStrongMatchThreshold { get; set; } = HybridSearchEngine.DefaultSemanticStrongMatchThreshold;

    public double SemanticBoostFactor { get; set; } = HybridSearchEngine.DefaultSemanticBoostFactor;

    /// <summary>
    /// Minimum raw cosine similarity for a vector hit to count as a signal at all. Below it the
    /// candidate's vector score is zeroed, and a candidate with no lexical match either is dropped
    /// rather than min-max normalized back up into the results.
    /// </summary>
    public double VectorRelevanceFloor { get; set; } = HybridSearchEngine.DefaultVectorRelevanceFloor;
}
