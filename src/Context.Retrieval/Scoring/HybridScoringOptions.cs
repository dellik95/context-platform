namespace Context.Retrieval.Scoring;

/// <summary>
/// Configurable weights for <see cref="HybridScorer"/>. Defaults bias semantic retrieval
/// (w_lex=0.20, w_vec=0.45, w_graph=0.2, w_rec=0.1, w_mem=0.05 — sums to 1.0) and can be
/// overridden via options binding.
/// </summary>
public sealed class HybridScoringOptions
{
    public double LexicalWeight { get; set; } = HybridScorer.DefaultLexicalWeight;
    public double VectorWeight { get; set; } = HybridScorer.DefaultVectorWeight;
    public double GraphWeight { get; set; } = HybridScorer.DefaultGraphWeight;
    public double RecencyWeight { get; set; } = HybridScorer.DefaultRecencyWeight;
    public double MemoryWeight { get; set; } = HybridScorer.DefaultMemoryWeight;
}
