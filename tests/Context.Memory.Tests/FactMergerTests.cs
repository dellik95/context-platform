using Context.Memory.Consolidation;
using FluentAssertions;

namespace Context.Memory.Tests;

public sealed class FactMergerTests
{
    [Fact]
    public void Merge_keeps_existing_facts_in_order_and_appends_new_ones()
    {
        FactMerger.Merge(["Webhook retries five times."], ["Timeout is 30 seconds."])
            .Should().Equal("Webhook retries five times.", "Timeout is 30 seconds.");
    }

    [Fact]
    public void Merge_drops_reworded_duplicates_and_keeps_the_existing_wording()
    {
        var merged = FactMerger.Merge(
            ["The webhook client retries five times."],
            ["webhook client retries five times", "WEBHOOK CLIENT RETRIES FIVE TIMES!"]);

        merged.Should().Equal("The webhook client retries five times.");
    }

    [Fact]
    public void Merge_trims_and_skips_blank_facts()
    {
        FactMerger.Merge(["  Retries are capped.  ", ""], ["   "]).Should().Equal("Retries are capped.");
    }

    [Theory]
    [InlineData("Retries five times", "retries five times.", true)]
    [InlineData("The webhook client retries five times", "Webhook client retries five times", true)]
    [InlineData("Retries five times", "Retries three times", false)]
    public void AreNearDuplicates_compares_normalized_token_sets(string a, string b, bool expected)
    {
        FactMerger.AreNearDuplicates(a, b).Should().Be(expected);
    }

    [Fact]
    public void NormalizeFact_lowercases_and_keeps_only_word_tokens()
    {
        FactMerger.NormalizeFact("Don't  retry, EVER!").Should().Be("don't retry ever");
    }
}
