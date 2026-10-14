using Context.Memory.Consolidation;
using FluentAssertions;

namespace Context.Memory.Tests;

public sealed class MemoryMergeOptionsTests
{
    [Fact]
    public void Defaults_are_inside_every_limit()
    {
        MemoryMergeOptions.Default.Normalized().Should().Be(MemoryMergeOptions.Default);
    }

    [Fact]
    public void Normalized_clamps_every_numeric_value_into_its_range()
    {
        var low = new MemoryMergeOptions(true, 0, 1, 0, 0, -1, 0).Normalized();
        low.Should().Be(new MemoryMergeOptions(
            true, MemoryMergeOptions.MinOnlineMaxSources, MemoryMergeOptions.MinBodyChars, MemoryMergeOptions.MinFacts,
            MemoryMergeOptions.MinGroupSize, MemoryMergeOptions.MinSimilarity, MemoryMergeOptions.MinSourceExcerptChars));

        var high = new MemoryMergeOptions(false, 99, 999_999, 999, 999, 5, 999_999).Normalized();
        high.Should().Be(new MemoryMergeOptions(
            false, MemoryMergeOptions.MaxOnlineMaxSources, MemoryMergeOptions.MaxBodyCharsLimit, MemoryMergeOptions.MaxFactsLimit,
            MemoryMergeOptions.MaxGroupSizeLimit, MemoryMergeOptions.MaxSimilarity, MemoryMergeOptions.MaxSourceExcerptCharsLimit));
    }

    [Fact]
    public void Normalized_replaces_a_non_numeric_similarity_with_the_default()
    {
        (MemoryMergeOptions.Default with { ReconsolidationMinSimilarity = double.NaN }).Normalized()
            .ReconsolidationMinSimilarity.Should().Be(MemoryMergeOptions.Default.ReconsolidationMinSimilarity);
    }
}
