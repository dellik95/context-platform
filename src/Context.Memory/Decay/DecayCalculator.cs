using Context.Domain.Enums;

namespace Context.Memory.Decay;

/// <summary>
/// Pure functions turning a memory's confidence, tier and reinforcement recency into a relevance
/// multiplier in [0, 1], and deciding when an unpinned memory has decayed out of the live set.
/// </summary>
public static class DecayCalculator
{
    public static readonly TimeSpan WorkingHalfLife = TimeSpan.FromDays(30);
    public static readonly TimeSpan LongTermHalfLife = TimeSpan.FromDays(90);

    /// <summary>Working knowledge below this relevance is retired to Obsolete.</summary>
    public const double ObsoleteRelevanceThreshold = 0.15;

    /// <summary>Events older than this (since last reinforced) are retired to Obsolete.</summary>
    public static readonly TimeSpan EventRetention = TimeSpan.FromDays(90);

    public static TimeSpan HalfLifeFor(MemoryTier tier) => tier == MemoryTier.LongTerm ? LongTermHalfLife : WorkingHalfLife;

    /// <summary><c>0.5 ^ (age / halfLife(tier))</c>, or 1.0 for a pinned memory.</summary>
    public static double ComputeDecayFactor(DateTimeOffset lastReinforcedAt, bool pinned, MemoryTier tier, DateTimeOffset now)
    {
        if (pinned)
        {
            return 1.0;
        }

        var ageDays = (now - lastReinforcedAt).TotalDays;
        return ageDays <= 0 ? 1.0 : Math.Pow(0.5, ageDays / HalfLifeFor(tier).TotalDays);
    }

    /// <summary><c>clamp(confidence, 0, 1) * decayFactor</c>.</summary>
    public static double ComputeRelevanceMultiplier(double confidence, DateTimeOffset lastReinforcedAt, bool pinned, MemoryTier tier, DateTimeOffset now) =>
        Math.Clamp(confidence, 0.0, 1.0) * ComputeDecayFactor(lastReinforcedAt, pinned, tier, now);

    /// <summary>Whether the decay job should retire <paramref name="memory"/>: events past
    /// <see cref="EventRetention"/>, Working knowledge below <see cref="ObsoleteRelevanceThreshold"/>.
    /// Pinned and LongTerm knowledge never expire.</summary>
    public static bool IsExpired(Context.Domain.Entities.Memory memory, DateTimeOffset now)
    {
        if (memory.Pinned)
        {
            return false;
        }

        if (memory.Type.IsEvent())
        {
            return now - memory.LastReinforcedAt > EventRetention;
        }

        return memory.Tier == MemoryTier.Working
            && ComputeRelevanceMultiplier(memory.Confidence, memory.LastReinforcedAt, false, memory.Tier, now) < ObsoleteRelevanceThreshold;
    }
}
