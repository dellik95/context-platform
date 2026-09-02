using Context.Domain.Enums;
using Context.Retrieval.Scoring;
using FluentAssertions;

namespace Context.Retrieval.Tests.Scoring;

public class HybridScorerTests
{
    [Fact]
    public void DefaultWeights_SumToOne()
    {
        var sum = HybridScorer.DefaultLexicalWeight
            + HybridScorer.DefaultVectorWeight
            + HybridScorer.DefaultGraphWeight
            + HybridScorer.DefaultRecencyWeight
            + HybridScorer.DefaultMemoryWeight;

        sum.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void DefaultMemoryWeight_IsZero_BecauseNoPerCandidateSignalExistsForCodeSearch()
    {
        // BACKLOG-13 (ARCHITECTURE_ASSESSMENT.md RAG-1): memory-adjacency has no per-candidate
        // data source in the code-search path (Memory.FilesReadJson/FilesModifiedJson are
        // write-only path lists, never joined to Chunk/File). Rather than let a constant
        // placeholder silently normalize to a non-zero 0.5 contribution via NormalizeMinMax's
        // all-equal fallback, the weight is explicitly zero so it is honestly inert.
        HybridScorer.DefaultMemoryWeight.Should().Be(0.0);
    }

    [Fact]
    public void Combine_DefaultOptions_MemorySignalHasNoEffectOnScore()
    {
        var scorer = new HybridScorer();

        var lowMemory = scorer.Combine(new NormalizedSignals(0.5, 0.5, 0.5, 0.5, Memory01: 0.0));
        var highMemory = scorer.Combine(new NormalizedSignals(0.5, 0.5, 0.5, 0.5, Memory01: 1.0));

        highMemory.Should().Be(lowMemory, "DefaultMemoryWeight is 0, so varying Memory01 must never change the combined score");
    }

    [Fact]
    public void Combine_AllZeroSignals_ReturnsZero()
    {
        var scorer = new HybridScorer();

        var result = scorer.Combine(new NormalizedSignals(0, 0, 0, 0, 0));

        result.Should().Be(0.0);
    }

    [Fact]
    public void Combine_AllOneSignals_WithDefaultWeights_ReturnsOne()
    {
        var scorer = new HybridScorer();

        var result = scorer.Combine(new NormalizedSignals(1, 1, 1, 1, 1));

        result.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Combine_MixedSignals_MatchesWeightedSumFormula()
    {
        var options = new HybridScoringOptions
        {
            LexicalWeight = 0.3,
            VectorWeight = 0.3,
            GraphWeight = 0.2,
            RecencyWeight = 0.1,
            MemoryWeight = 0.1,
        };
        var scorer = new HybridScorer(options);
        var signals = new NormalizedSignals(Lexical01: 0.5, Vector01: 0.8, Graph01: 0.2, Recency01: 0.9, Memory01: 0.1);

        var expected = (0.3 * 0.5) + (0.3 * 0.8) + (0.2 * 0.2) + (0.1 * 0.9) + (0.1 * 0.1);

        scorer.Combine(signals).Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(-0.0001)]
    [InlineData(1.0001)]
    [InlineData(2.0)]
    public void Combine_OutOfRangeSignals_AreClampedTo01BeforeWeighting(double outOfRange)
    {
        // Default weights sum to (approximately) 1.0, so combining an out-of-range value across
        // every signal is equivalent to clamping that value directly.
        var scorer = new HybridScorer();
        var signals = new NormalizedSignals(outOfRange, outOfRange, outOfRange, outOfRange, outOfRange);

        var result = scorer.Combine(signals);

        var expectedClamped = Math.Clamp(outOfRange, 0.0, 1.0);
        result.Should().BeApproximately(expectedClamped, 1e-9);
    }

    [Fact]
    public void Combine_ResultIsAlwaysClampedWithinUnitRange()
    {
        var scorer = new HybridScorer();

        var result = scorer.Combine(new NormalizedSignals(1, 1, 1, 1, 1));

        result.Should().BeInRange(0.0, 1.0);
    }

    [Fact]
    public void Combine_ZeroWeightOptions_ReturnsZeroRegardlessOfSignals()
    {
        var options = new HybridScoringOptions
        {
            LexicalWeight = 0,
            VectorWeight = 0,
            GraphWeight = 0,
            RecencyWeight = 0,
            MemoryWeight = 0,
        };
        var scorer = new HybridScorer(options);

        var result = scorer.Combine(new NormalizedSignals(1, 1, 1, 1, 1));

        result.Should().Be(0.0);
    }

    [Fact]
    public void Constructor_NullOptions_FallsBackToDefaultWeights()
    {
        var scorer = new HybridScorer(options: null);
        var signals = new NormalizedSignals(1, 0, 0, 0, 0);

        var result = scorer.Combine(signals);

        result.Should().BeApproximately(HybridScorer.DefaultLexicalWeight, 1e-9);
    }

    [Fact]
    public void NormalizeMinMax_EmptyInput_ReturnsEmpty()
    {
        var result = HybridScorer.NormalizeMinMax([]);

        result.Should().BeEmpty();
    }

    [Fact]
    public void NormalizeMinMax_AllEqualNonZeroValues_NormalizeToNeutralMidpoint()
    {
        var result = HybridScorer.NormalizeMinMax([3.0, 3.0, 3.0]);

        result.Should().AllSatisfy(v => v.Should().Be(0.5));
    }

    [Fact]
    public void NormalizeMinMax_AllZeroValues_NormalizeToNeutralMidpoint()
    {
        var result = HybridScorer.NormalizeMinMax([0.0, 0.0, 0.0]);

        result.Should().AllSatisfy(v => v.Should().Be(0.5));
    }

    [Fact]
    public void NormalizeMinMax_SingleValue_NormalizesToNeutralMidpoint()
    {
        var result = HybridScorer.NormalizeMinMax([7.5]);

        result.Should().ContainSingle().Which.Should().Be(0.5);
    }

    [Fact]
    public void NormalizeMinMax_DistinctValues_MapsMinToZeroAndMaxToOne()
    {
        var result = HybridScorer.NormalizeMinMax([1.0, 5.0, 10.0]);

        result[0].Should().Be(0.0);
        result[2].Should().Be(1.0);
        result[1].Should().BeApproximately((5.0 - 1.0) / (10.0 - 1.0), 1e-9);
    }

    [Fact]
    public void NormalizeMinMax_PreservesCountAndRelativeOrderIncludingTies()
    {
        double[] raw = [4.0, 1.0, 9.0, 4.0];

        var result = HybridScorer.NormalizeMinMax(raw);

        result.Should().HaveCount(raw.Length);
        result.Should().AllSatisfy(v => v.Should().BeInRange(0.0, 1.0));
        result[2].Should().Be(1.0); // max
        result[1].Should().Be(0.0); // min
        result[0].Should().Be(result[3]); // equal raw inputs normalize equally
        result[1].Should().BeLessThan(result[0]);
    }

    // ---- ApplyMemoryTypeBonus (codemem-inspired kind-based re-rank bonus) ------------------

    [Fact]
    public void ApplyMemoryTypeBonus_DecisionRanksAboveOtherwiseIdenticalChange()
    {
        const double baseScore = 0.5;

        var decisionScore = HybridScorer.ApplyMemoryTypeBonus(baseScore, MemoryType.Decision);
        var changeScore = HybridScorer.ApplyMemoryTypeBonus(baseScore, MemoryType.Change);

        decisionScore.Should().BeGreaterThan(changeScore);
        changeScore.Should().Be(baseScore); // Change carries no bonus.
    }

    [Theory]
    [InlineData(MemoryType.Decision, 0.15)]
    [InlineData(MemoryType.Architecture, 0.15)]
    [InlineData(MemoryType.BugFix, 0.10)]
    [InlineData(MemoryType.Issue, 0.10)]
    [InlineData(MemoryType.Feature, 0.05)]
    [InlineData(MemoryType.Refactor, 0.05)]
    [InlineData(MemoryType.Discovery, 0.05)]
    [InlineData(MemoryType.Change, 0.0)]
    [InlineData(MemoryType.Exploration, 0.0)]
    public void ApplyMemoryTypeBonus_MatchesDocumentedConstantPerMemoryType(MemoryType type, double expectedBonus)
    {
        const double baseScore = 0.4;

        var result = HybridScorer.ApplyMemoryTypeBonus(baseScore, type);

        result.Should().BeApproximately(baseScore + expectedBonus, 1e-9);
    }

    [Fact]
    public void ApplyMemoryTypeBonus_NullMemoryType_AddsNoBonus()
    {
        const double baseScore = 0.42;

        var result = HybridScorer.ApplyMemoryTypeBonus(baseScore, null);

        result.Should().Be(baseScore);
    }

    [Fact]
    public void ApplyMemoryTypeBonus_ResultIsClampedTo01_EvenWhenBaseScoreIsAlreadyNearOne()
    {
        var result = HybridScorer.ApplyMemoryTypeBonus(0.95, MemoryType.Decision);

        result.Should().Be(1.0);
    }

    [Fact]
    public void ApplyMemoryTypeBonus_DecisionAndArchitecture_ShareTheHighestBonus()
    {
        const double baseScore = 0.3;

        var decision = HybridScorer.ApplyMemoryTypeBonus(baseScore, MemoryType.Decision);
        var architecture = HybridScorer.ApplyMemoryTypeBonus(baseScore, MemoryType.Architecture);

        decision.Should().Be(architecture);
    }

    [Fact]
    public void ApplyMemoryTypeBonus_BonusOrdering_DecisionAboveBugFixAboveFeatureAboveChange()
    {
        const double baseScore = 0.2;

        var decision = HybridScorer.ApplyMemoryTypeBonus(baseScore, MemoryType.Decision);
        var bugFix = HybridScorer.ApplyMemoryTypeBonus(baseScore, MemoryType.BugFix);
        var feature = HybridScorer.ApplyMemoryTypeBonus(baseScore, MemoryType.Feature);
        var change = HybridScorer.ApplyMemoryTypeBonus(baseScore, MemoryType.Change);

        decision.Should().BeGreaterThan(bugFix);
        bugFix.Should().BeGreaterThan(feature);
        feature.Should().BeGreaterThan(change);
    }
}
