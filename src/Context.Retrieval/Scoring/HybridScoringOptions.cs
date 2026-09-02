namespace Context.Retrieval.Scoring;

/// <summary>
/// Configurable weights for <see cref="HybridScorer"/>. Defaults bias semantic retrieval
/// (w_lex≈0.2353, w_vec≈0.5294, w_graph≈0.2353, w_rec=0, w_mem=0 — sums to 1.0) and can be
/// overridden via options binding. w_rec and w_mem are zero by design (see
/// <see cref="HybridScorer"/>'s remarks on <c>DefaultRecencyWeight</c>/<c>DefaultMemoryWeight</c>)
/// until each has a real per-candidate signal that is actually fit for ranking code search.
/// </summary>
public sealed class HybridScoringOptions
{
    public double LexicalWeight { get; set; } = HybridScorer.DefaultLexicalWeight;
    public double VectorWeight { get; set; } = HybridScorer.DefaultVectorWeight;
    public double GraphWeight { get; set; } = HybridScorer.DefaultGraphWeight;
    public double RecencyWeight { get; set; } = HybridScorer.DefaultRecencyWeight;
    public double MemoryWeight { get; set; } = HybridScorer.DefaultMemoryWeight;
}
