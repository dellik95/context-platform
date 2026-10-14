using Context.Memory.Consolidation;
using FluentAssertions;

namespace Context.Memory.Tests;

public sealed class MergeVerifierTests
{
    [Fact]
    public void MissingFacts_lists_source_facts_no_merged_fact_covers()
    {
        var missing = MergeVerifier.MissingFacts(
            ["Retries stop at the first 4xx response", "Backoff starts at 2 seconds"],
            ["Backoff starts at 2 seconds and doubles each attempt", "Something unrelated"]);

        missing.Should().Equal("Retries stop at the first 4xx response");
    }

    [Fact]
    public void MissingFacts_accepts_a_fact_contained_word_for_word_in_a_longer_merged_fact()
    {
        MergeVerifier.MissingFacts(["Backoff starts at 2 seconds"], ["Backoff starts at 2 seconds and doubles on every failed delivery attempt"])
            .Should().BeEmpty();
    }

    [Fact]
    public void MissingNames_reports_strong_identifiers_the_merged_text_dropped()
    {
        var missing = MergeVerifier.MissingNames(
            ["WebhookVerifier reads PAYMENTS_WEBHOOK_SECRET from src/Payments/WebhookVerifier.cs and tracks PAY-123."],
            ["The verifier reads its secret from configuration."]);

        missing.Should().Contain("PAYMENTS_WEBHOOK_SECRET").And.Contain("PAY-123");
    }

    [Fact]
    public void MissingNames_counts_a_full_path_as_naming_its_file()
    {
        MergeVerifier.MissingNames(["See WebhookVerifier.cs for the check."], ["The check lives in src/Payments/WebhookVerifier.cs."])
            .Should().BeEmpty();
    }

    [Fact]
    public void AppendNames_adds_a_closing_line_and_refuses_to_pass_the_limit()
    {
        MergeVerifier.AppendNames("Body.", ["A.cs", "PAY-1"], 200).Should().Be("Body.\n\nAlso referenced: A.cs, PAY-1.");
        MergeVerifier.AppendNames("Body.", ["A.cs"], 10).Should().BeNull();
        MergeVerifier.AppendNames("Body.", [], 3).Should().Be("Body.");
    }
}
