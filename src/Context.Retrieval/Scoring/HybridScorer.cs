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
    // The original weights below (lex 0.20,
    // vec 0.45, graph 0.20, rec 0.10, mem 0.05) were set when both recency and memory were
    // constant placeholder signals -- NormalizeMinMax's all-equal fallback turned each into a
    // silent, inert 0.5 contribution.
    //
    // Recency was briefly wired to a "real" per-candidate signal sourced from the owning file's
    // UpdatedAt (HybridSearchEngine.ScoreAndRank), and measured against the golden-query harness
    // (RetrievalQualityHarnessTests): hit-rate@5 0.619 -> 0.524, MRR 0.644 -> 0.551 -- a regression,
    // not an improvement. Root cause is a semantic conflict, not just an unlucky benchmark:
    // File.UpdatedAt is stamped at index-processing time (IndexingPipeline.IndexFileInternalAsync)
    // and is already used elsewhere (ContextEngine.ProjectInfoAsync's LastIndexedAt) to mean "when
    // this file was last (re)indexed," not "when its source content last changed." A full reindex
    // -- including the harness's, and any bulk (re)import -- stamps every file with a near-identical
    // "just indexed" timestamp, which NormalizeMinMax then stretches to fill [0, 1] and the recency
    // weight injects as pure noise. Even in steady-state incremental indexing this signal would only
    // reflect indexing cadence, not actual content freshness, unless a file is re-indexed every time
    // it is touched -- true here, but conflating "last indexed" and "last changed" is the wrong
    // timestamp to reuse for this purpose regardless. Introducing a genuinely separate
    // source-last-modified field would need a new Domain/File column, an EF migration, and changes
    // through IndexingPipeline/ISearchIndex/SqliteSearchIndex/HybridSearchEngine -- too much surface
    // for a clean, small addition, so recency is reverted to the same honest-zero treatment as
    // memory below rather than kept half-wired.
    //
    // Memory still has no per-candidate data source for code search (Memory.FilesReadJson/
    // FilesModifiedJson are write-only path lists with no FK to Chunk/File), so rather than let it
    // contribute a misleading non-zero constant, its weight is honestly zeroed, and now recency's is
    // too, with the remaining three weights rescaled to sum back to 1.0 (0.20/0.85, 0.45/0.85, 0.20/0.85).
    public const double DefaultLexicalWeight = 0.20 / 0.85;
    public const double DefaultVectorWeight = 0.45 / 0.85;
    public const double DefaultGraphWeight = 0.20 / 0.85;
    public const double DefaultRecencyWeight = 0.0;
    public const double DefaultMemoryWeight = 0.0;

    /// <summary>
    /// Small additive kind-based re-rank nudge favoring durable knowledge over activity records:
    /// decisions, constraints and architecture highest, other knowledge kinds next, generic facts a
    /// little, events none. Used by memory ranking through <see cref="NormalizedMemoryTypeBonus"/>,
    /// not folded into <see cref="Combine"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<MemoryType, double> MemoryTypeBonus = new Dictionary<MemoryType, double>
    {
        [MemoryType.Decision] = 0.15,
        [MemoryType.Constraint] = 0.15,
        [MemoryType.Architecture] = 0.15,
        [MemoryType.Behavior] = 0.10,
        [MemoryType.Discovery] = 0.10,
        [MemoryType.Configuration] = 0.10,
        [MemoryType.Procedure] = 0.10,
        [MemoryType.Issue] = 0.10,
        [MemoryType.Fact] = 0.05,
        // MemoryType.Event is intentionally absent -> 0.0 bonus.
    };

    private static readonly double MaxMemoryTypeBonus = MemoryTypeBonus.Values.Max();

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

    /// <summary><see cref="MemoryTypeBonus"/> scaled to [0, 1] against the largest bonus.</summary>
    public static double NormalizedMemoryTypeBonus(MemoryType type) =>
        MemoryTypeBonus.TryGetValue(type, out var bonus) ? bonus / MaxMemoryTypeBonus : 0.0;

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
