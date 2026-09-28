using Context.Domain.Enums;
using Context.Memory.Decay;
using FluentAssertions;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Memory.Tests;

public sealed class DecayCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ComputeRelevanceMultiplier_AtZeroAge_EqualsConfidence()
    {
        var result = DecayCalculator.ComputeRelevanceMultiplier(0.8, Now, pinned: false, MemoryTier.Working, Now);

        result.Should().BeApproximately(0.8, 1e-9);
    }

    [Theory]
    [InlineData(MemoryTier.Working, 30)]
    [InlineData(MemoryTier.LongTerm, 90)]
    public void HalfLife_depends_on_the_tier(MemoryTier tier, int days)
    {
        DecayCalculator.HalfLifeFor(tier).Should().Be(TimeSpan.FromDays(days));

        DecayCalculator.ComputeRelevanceMultiplier(0.8, Now - TimeSpan.FromDays(days), pinned: false, tier, Now)
            .Should().BeApproximately(0.4, 1e-6);
        DecayCalculator.ComputeRelevanceMultiplier(1.0, Now - TimeSpan.FromDays(days * 2), pinned: false, tier, Now)
            .Should().BeApproximately(0.25, 1e-6);
    }

    [Fact]
    public void ComputeRelevanceMultiplier_WhenPinned_IgnoresAge()
    {
        var result = DecayCalculator.ComputeRelevanceMultiplier(0.7, Now - TimeSpan.FromDays(3650), pinned: true, MemoryTier.Working, Now);

        result.Should().BeApproximately(0.7, 1e-9);
    }

    [Theory]
    [InlineData(1.5, 1.0)]
    [InlineData(-0.5, 0.0)]
    public void ComputeRelevanceMultiplier_ClampsConfidence(double confidence, double expected)
    {
        DecayCalculator.ComputeRelevanceMultiplier(confidence, Now, pinned: false, MemoryTier.Working, Now)
            .Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void ComputeDecayFactor_FutureLastReinforcedAt_ReturnsOne()
    {
        DecayCalculator.ComputeDecayFactor(Now + TimeSpan.FromDays(5), pinned: false, MemoryTier.Working, Now).Should().Be(1.0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(365)]
    public void ComputeDecayFactor_StrictlyDecreasesWithAge(int laterAgeDays)
    {
        var earlier = DecayCalculator.ComputeDecayFactor(Now - TimeSpan.FromDays(laterAgeDays - 1), pinned: false, MemoryTier.Working, Now);
        var later = DecayCalculator.ComputeDecayFactor(Now - TimeSpan.FromDays(laterAgeDays), pinned: false, MemoryTier.Working, Now);

        later.Should().BeLessThan(earlier);
    }

    [Fact]
    public void ComputeDecayFactor_LongTerm_decays_slower_than_Working()
    {
        var lastReinforcedAt = Now - TimeSpan.FromDays(45);

        DecayCalculator.ComputeDecayFactor(lastReinforcedAt, pinned: false, MemoryTier.LongTerm, Now)
            .Should().BeGreaterThan(DecayCalculator.ComputeDecayFactor(lastReinforcedAt, pinned: false, MemoryTier.Working, Now));
    }

    [Fact]
    public void ComputeDecayFactor_Reinforcement_ResetsDecay()
    {
        var stale = DecayCalculator.ComputeDecayFactor(Now - TimeSpan.FromDays(90), pinned: false, MemoryTier.Working, Now);
        var reset = DecayCalculator.ComputeDecayFactor(Now, pinned: false, MemoryTier.Working, Now);
        var resumed = DecayCalculator.ComputeDecayFactor(Now, pinned: false, MemoryTier.Working, Now + DecayCalculator.WorkingHalfLife);

        stale.Should().BeLessThan(1.0);
        reset.Should().Be(1.0);
        resumed.Should().BeApproximately(0.5, 1e-6);
    }

    [Fact]
    public void IsExpired_retires_working_knowledge_below_the_relevance_threshold()
    {
        // 0.7 * 0.5^(90/30) = 0.0875 < 0.15; 0.7 * 0.5^(60/30) = 0.175.
        DecayCalculator.IsExpired(Knowledge(MemoryTier.Working, ageDays: 90), Now).Should().BeTrue();
        DecayCalculator.IsExpired(Knowledge(MemoryTier.Working, ageDays: 60), Now).Should().BeFalse();
    }

    [Fact]
    public void IsExpired_never_retires_long_term_or_pinned_knowledge()
    {
        DecayCalculator.IsExpired(Knowledge(MemoryTier.LongTerm, ageDays: 3650), Now).Should().BeFalse();
        DecayCalculator.IsExpired(Knowledge(MemoryTier.Working, ageDays: 3650, pinned: true), Now).Should().BeFalse();
    }

    [Fact]
    public void IsExpired_retires_events_after_the_retention_window_regardless_of_confidence()
    {
        var old = Knowledge(MemoryTier.Working, ageDays: 91);
        old.Type = MemoryType.Event;
        old.Confidence = 0.95;
        var recent = Knowledge(MemoryTier.Working, ageDays: 89);
        recent.Type = MemoryType.Event;
        recent.Confidence = 0.05;

        DecayCalculator.IsExpired(old, Now).Should().BeTrue();
        DecayCalculator.IsExpired(recent, Now).Should().BeFalse();
    }

    private static DomainMemory Knowledge(MemoryTier tier, int ageDays, bool pinned = false) => new()
    {
        Title = "Payments webhook retries",
        Body = "PaymentsWebhookClient retries five times.",
        Type = MemoryType.Configuration,
        Tier = tier,
        Pinned = pinned,
        Confidence = 0.7,
        LastReinforcedAt = Now - TimeSpan.FromDays(ageDays),
    };
}
