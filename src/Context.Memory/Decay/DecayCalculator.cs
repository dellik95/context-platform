namespace Context.Memory.Decay;

/// <summary>
/// Pure function turning a memory's confidence, reinforcement recency and pinned flag into
/// a single relevance multiplier in [0, 1] that Context.Retrieval (via Context.Application)
/// applies on top of a base search score. Not a service — no DI registration needed.
/// </summary>
public static class DecayCalculator
{
    /// <summary>Approximate half-life of an unreinforced, unpinned memory's relevance.</summary>
    public static readonly TimeSpan HalfLife = TimeSpan.FromDays(30);

    /// <summary>
    /// Time-decay factor alone (ignores confidence): 1.0 for a pinned memory, otherwise an
    /// exponential decay of <c>0.5 ^ (age / HalfLife)</c> based on how long ago the memory
    /// was last reinforced.
    /// </summary>
    public static double ComputeDecayFactor(DateTimeOffset lastReinforcedAt, bool pinned, DateTimeOffset now)
    {
        if (pinned)
        {
            return 1.0;
        }

        var ageDays = (now - lastReinforcedAt).TotalDays;
        if (ageDays <= 0)
        {
            return 1.0;
        }

        var halfLifeDays = HalfLife.TotalDays;
        return Math.Pow(0.5, ageDays / halfLifeDays);
    }

    /// <summary>
    /// The relevance multiplier a search/ranking layer should apply for a memory:
    /// <c>clamp(confidence, 0, 1) * decayFactor</c>. Pinned memories always use a decay
    /// factor of 1.0, so only their confidence bounds the result.
    /// </summary>
    public static double ComputeRelevanceMultiplier(
        double confidence,
        DateTimeOffset lastReinforcedAt,
        bool pinned,
        DateTimeOffset now)
    {
        var clampedConfidence = Math.Clamp(confidence, 0.0, 1.0);
        var decayFactor = ComputeDecayFactor(lastReinforcedAt, pinned, now);
        return clampedConfidence * decayFactor;
    }
}
