namespace Context.Retrieval.Scoring;

/// <summary>
/// Configurable weights for <see cref="HybridScorer"/>. Defaults bias semantic retrieval
/// (w_lex≈0.2105, w_vec≈0.4737, w_graph≈0.2105, w_rec≈0.1053, w_mem=0 — sums to 1.0) and can be
/// overridden via options binding. w_mem is zero by design (see <see cref="HybridScorer"/>'s
/// remarks on <c>DefaultMemoryWeight</c>) until a real per-candidate memory-adjacency signal
/// exists for code search.
/// </summary>
public sealed class HybridScoringOptions
{
    public double LexicalWeight { get; set; } = HybridScorer.DefaultLexicalWeight;
    public double VectorWeight { get; set; } = HybridScorer.DefaultVectorWeight;
    public double GraphWeight { get; set; } = HybridScorer.DefaultGraphWeight;
    public double RecencyWeight { get; set; } = HybridScorer.DefaultRecencyWeight;
    public double MemoryWeight { get; set; } = HybridScorer.DefaultMemoryWeight;
}
