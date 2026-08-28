namespace Context.Retrieval.Scoring;

/// <summary>
/// Implements the weighted-sum hybrid ranking formula from spec §9.6:
/// <c>score = w_lex*lexical + w_vec*vector + w_graph*graph_proximity + w_rec*recency + w_mem*memory</c>,
/// operating on signals already normalized to [0, 1] via <see cref="NormalizeMinMax"/>.
/// </summary>
public sealed class HybridScorer : IHybridScorer
{
    public const double DefaultLexicalWeight = 0.25;
    public const double DefaultVectorWeight = 0.35;
    public const double DefaultGraphWeight = 0.20;
    public const double DefaultRecencyWeight = 0.10;
    public const double DefaultMemoryWeight = 0.10;

    private readonly HybridScoringOptions _options;

    public HybridScorer(HybridScoringOptions? options = null)
    {
        _options = options ?? new HybridScoringOptions();
    }

    public double Combine(NormalizedSignals signals)
    {
        var score =
            _options.LexicalWeight * Clamp01(signals.Lexical01) +
            _options.VectorWeight * Clamp01(signals.Vector01) +
            _options.GraphWeight * Clamp01(signals.Graph01) +
            _options.RecencyWeight * Clamp01(signals.Recency01) +
            _options.MemoryWeight * Clamp01(signals.Memory01);

        return Clamp01(score);
    }

    /// <summary>
    /// Min-max normalizes a batch of raw signal values (BM25 scores, cosine similarities, etc.)
    /// to [0, 1] relative to each other, since each raw signal has a different, provider-specific
    /// scale. When every value in the batch is (approximately) equal — including the common case
    /// of an all-zero placeholder signal — every element normalizes to 0.5: a neutral midpoint
    /// that adds an identical constant to every candidate's score and therefore never changes
    /// relative ranking, only the reported absolute score.
    /// </summary>
    public static IReadOnlyList<double> NormalizeMinMax(IReadOnlyList<double> raw)
    {
        if (raw.Count == 0)
            return [];

        var min = raw[0];
        var max = raw[0];
        foreach (var value in raw)
        {
            if (value < min) min = value;
            if (value > max) max = value;
        }

        var range = max - min;
        if (range < 1e-9)
            return [.. raw.Select(_ => 0.5)];

        return [.. raw.Select(v => (v - min) / range)];
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);
}
