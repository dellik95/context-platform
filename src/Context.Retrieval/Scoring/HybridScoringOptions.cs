namespace Context.Retrieval.Scoring;

/// <summary>
/// Configurable weights for <see cref="HybridScorer"/>. Defaults match spec §9.6's hybrid
/// retrieval formula (w_lex=0.25, w_vec=0.35, w_graph=0.2, w_rec=0.1, w_mem=0.1 — sums to 1.0)
/// and can be overridden via configuration binding (e.g. <c>IOptions&lt;HybridScoringOptions&gt;</c>)
/// at composition time.
/// </summary>
public sealed class HybridScoringOptions
{
    public double LexicalWeight { get; init; } = HybridScorer.DefaultLexicalWeight;
    public double VectorWeight { get; init; } = HybridScorer.DefaultVectorWeight;
    public double GraphWeight { get; init; } = HybridScorer.DefaultGraphWeight;
    public double RecencyWeight { get; init; } = HybridScorer.DefaultRecencyWeight;
    public double MemoryWeight { get; init; } = HybridScorer.DefaultMemoryWeight;
}
