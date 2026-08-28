using Context.Memory.Decay;
using FluentAssertions;

namespace Context.Memory.Tests;

public class DecayCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ComputeRelevanceMultiplier_AtZeroAge_EqualsConfidence()
    {
        var result = DecayCalculator.ComputeRelevanceMultiplier(0.8, Now, pinned: false, Now);

        result.Should().BeApproximately(0.8, 1e-9);
    }

    [Fact]
    public void ComputeRelevanceMultiplier_AtOneHalfLife_HalvesConfidence()
    {
        var lastReinforcedAt = Now - DecayCalculator.HalfLife;

        var result = DecayCalculator.ComputeRelevanceMultiplier(0.8, lastReinforcedAt, pinned: false, Now);

        result.Should().BeApproximately(0.4, 1e-6);
    }

    [Fact]
    public void ComputeRelevanceMultiplier_AtTwoHalfLives_QuartersConfidence()
    {
        var lastReinforcedAt = Now - (DecayCalculator.HalfLife * 2);

        var result = DecayCalculator.ComputeRelevanceMultiplier(1.0, lastReinforcedAt, pinned: false, Now);

        result.Should().BeApproximately(0.25, 1e-6);
    }

    [Fact]
    public void ComputeRelevanceMultiplier_WhenPinned_IgnoresAge()
    {
        var longAgo = Now - TimeSpan.FromDays(3650);

        var result = DecayCalculator.ComputeRelevanceMultiplier(0.7, longAgo, pinned: true, Now);

        result.Should().BeApproximately(0.7, 1e-9);
    }

    [Fact]
    public void ComputeRelevanceMultiplier_ClampsConfidenceAboveOne()
    {
        var result = DecayCalculator.ComputeRelevanceMultiplier(1.5, Now, pinned: false, Now);

        result.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void ComputeRelevanceMultiplier_ClampsConfidenceBelowZero()
    {
        var result = DecayCalculator.ComputeRelevanceMultiplier(-0.5, Now, pinned: false, Now);

        result.Should().Be(0.0);
    }

    [Fact]
    public void ComputeDecayFactor_FutureLastReinforcedAt_ReturnsOne()
    {
        var future = Now + TimeSpan.FromDays(5);

        var factor = DecayCalculator.ComputeDecayFactor(future, pinned: false, Now);

        factor.Should().Be(1.0);
    }

    [Fact]
    public void ComputeDecayFactor_MonotonicallyDecreasesWithAge()
    {
        var factorAt10Days = DecayCalculator.ComputeDecayFactor(Now - TimeSpan.FromDays(10), pinned: false, Now);
        var factorAt40Days = DecayCalculator.ComputeDecayFactor(Now - TimeSpan.FromDays(40), pinned: false, Now);

        factorAt40Days.Should().BeLessThan(factorAt10Days);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(90)]
    [InlineData(365)]
    public void ComputeDecayFactor_MonotonicallyDecreasesAcrossManyAges(int laterAgeDays)
    {
        // Strictly decreasing at every step, not just between two hand-picked points.
        var earlierFactor = DecayCalculator.ComputeDecayFactor(Now - TimeSpan.FromDays(laterAgeDays - 1), pinned: false, Now);
        var laterFactor = DecayCalculator.ComputeDecayFactor(Now - TimeSpan.FromDays(laterAgeDays), pinned: false, Now);

        laterFactor.Should().BeLessThan(earlierFactor);
    }

    [Fact]
    public void ComputeDecayFactor_WhenPinned_AlwaysFullWeightRegardlessOfAge()
    {
        // A fake/deterministic clock: same "now" fed for a range of ages, no DateTimeOffset.UtcNow involved.
        var ages = new[] { 0, 1, 30, 365, 3650 };

        foreach (var ageDays in ages)
        {
            var lastReinforcedAt = Now - TimeSpan.FromDays(ageDays);

            var factor = DecayCalculator.ComputeDecayFactor(lastReinforcedAt, pinned: true, Now);

            factor.Should().Be(1.0, because: $"pinned memories ignore age (tested at {ageDays} days)");
        }
    }

    [Fact]
    public void ComputeDecayFactor_Reinforcement_ResetsDecayToFullWeight()
    {
        // Simulate: a memory was last reinforced long ago and has decayed well below full weight...
        var staleReinforcedAt = Now - (DecayCalculator.HalfLife * 3);
        var decayedFactor = DecayCalculator.ComputeDecayFactor(staleReinforcedAt, pinned: false, Now);
        decayedFactor.Should().BeLessThan(1.0);

        // ...then a reinforcement event bumps LastReinforcedAt to "now", which resets the factor to 1.0...
        var reinforcedAt = Now;
        var resetFactor = DecayCalculator.ComputeDecayFactor(reinforcedAt, pinned: false, Now);
        resetFactor.Should().Be(1.0);
        resetFactor.Should().BeGreaterThan(decayedFactor);

        // ...and decay then resumes from that new reinforcement point as time keeps moving forward.
        var afterOneHalfLife = Now + DecayCalculator.HalfLife;
        var factorAfterReinforcement = DecayCalculator.ComputeDecayFactor(reinforcedAt, pinned: false, afterOneHalfLife);
        factorAfterReinforcement.Should().BeApproximately(0.5, 1e-6);
    }

    [Fact]
    public void ComputeRelevanceMultiplier_Reinforcement_ResetsRelevanceEvenWithUnchangedConfidence()
    {
        const double confidence = 0.9;
        var staleReinforcedAt = Now - (DecayCalculator.HalfLife * 4);

        var beforeReinforcement = DecayCalculator.ComputeRelevanceMultiplier(confidence, staleReinforcedAt, pinned: false, Now);
        var afterReinforcement = DecayCalculator.ComputeRelevanceMultiplier(confidence, lastReinforcedAt: Now, pinned: false, Now);

        afterReinforcement.Should().BeApproximately(confidence, 1e-9);
        afterReinforcement.Should().BeGreaterThan(beforeReinforcement);
    }
}
