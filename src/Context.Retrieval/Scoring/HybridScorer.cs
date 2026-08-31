using Context.Domain.Enums;
using Microsoft.Extensions.Options;

namespace Context.Retrieval.Scoring;

/// <summary>
/// Implements the weighted-sum hybrid ranking formula from spec §9.6:
/// <c>score = w_lex*lexical + w_vec*vector + w_graph*graph_proximity + w_rec*recency + w_mem*memory</c>,
/// operating on signals already normalized to [0, 1] via <see cref="NormalizeMinMax"/>.
/// </summary>
public sealed class HybridScorer : IHybridScorer
{
    public const double DefaultLexicalWeight = 0.20;
    public const double DefaultVectorWeight = 0.45;
    public const double DefaultGraphWeight = 0.20;
    public const double DefaultRecencyWeight = 0.10;
    public const double DefaultMemoryWeight = 0.05;

    /// <summary>
    /// Codemem-inspired kind-based re-rank bonus (ROADMAP.md's "hybrid pack re-ranking
    /// refinements"): a small additive nudge favoring memory kinds worth surfacing over generic
    /// ones. Decision/Architecture (locked-in project knowledge) rank highest, BugFix/Issue
    /// (concrete, actionable) next, Feature/Refactor/Discovery a little, and anything else
    /// (Change/Exploration/...) gets none. Deliberately NOT folded into <see cref="Combine"/>'s
    /// base 5-signal formula -- see <see cref="ApplyMemoryTypeBonus"/>, which layers it on top of
    /// an already-normalized [0, 1] score instead.
    /// </summary>
    public static readonly IReadOnlyDictionary<MemoryType, double> MemoryTypeBonus = new Dictionary<MemoryType, double>
    {
        [MemoryType.Decision] = 0.15,
        [MemoryType.Architecture] = 0.15,
        [MemoryType.BugFix] = 0.10,
        [MemoryType.Issue] = 0.10,
        [MemoryType.Feature] = 0.05,
        [MemoryType.Refactor] = 0.05,
        [MemoryType.Discovery] = 0.05,
        // MemoryType.Change and MemoryType.Exploration are intentionally absent -> 0.0 bonus.
    };

    private readonly HybridScoringOptions _options;
    private readonly IOptionsMonitor<HybridScoringOptions>? _optionsMonitor;

    public HybridScorer(HybridScoringOptions? options = null)
    {
        _options = options ?? new HybridScoringOptions();
    }

    public HybridScorer(IOptionsMonitor<HybridScoringOptions> optionsMonitor)
    {
        _optionsMonitor = optionsMonitor;
        _options = optionsMonitor.CurrentValue;
    }

    public double Combine(NormalizedSignals signals)
    {
        var options = _optionsMonitor?.CurrentValue ?? _options;

        var score =
            options.LexicalWeight * Clamp01(signals.Lexical01) +
            options.VectorWeight * Clamp01(signals.Vector01) +
            options.GraphWeight * Clamp01(signals.Graph01) +
            options.RecencyWeight * Clamp01(signals.Recency01) +
            options.MemoryWeight * Clamp01(signals.Memory01);

        return Clamp01(score);
    }

    /// <summary>
    /// Applies <see cref="MemoryTypeBonus"/> on top of an already-normalized [0, 1] relevance
    /// score (e.g. <see cref="Combine"/>'s output, or <c>DecayCalculator.ComputeRelevanceMultiplier</c>
    /// for a memory-derived score), then re-clamps to [0, 1] -- same pattern as <see cref="Clamp01"/>
    /// guards every signal going into <see cref="Combine"/>. This is an additive refinement layered
    /// on top of the base formula, not a change to it.
    /// </summary>
    public static double ApplyMemoryTypeBonus(double normalizedScore, MemoryType? memoryType)
    {
        var bonus = memoryType is { } type && MemoryTypeBonus.TryGetValue(type, out var b) ? b : 0.0;
        return Clamp01(normalizedScore + bonus);
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
