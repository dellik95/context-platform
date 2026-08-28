namespace Context.Retrieval.Scoring;

/// <summary>
/// The five ranking signals from spec §9.6, each already normalized to [0, 1] (see
/// <see cref="HybridScorer.NormalizeMinMax"/>) and ready to combine via
/// <see cref="IHybridScorer.Combine"/>.
/// </summary>
/// <param name="Lexical01">SQLite FTS5 / BM25 relevance.</param>
/// <param name="Vector01">Embedding cosine-similarity relevance.</param>
/// <param name="Graph01">Graph proximity to the query's top lexical/vector hits.</param>
/// <param name="Recency01">How recently the underlying file/chunk changed.</param>
/// <param name="Memory01">Relevance of historical project memory (Context.Memory), if any.</param>
public sealed record NormalizedSignals(double Lexical01, double Vector01, double Graph01, double Recency01, double Memory01);
