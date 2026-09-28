using Context.Domain.Enums;
using Context.Memory.Consolidation;
using FluentAssertions;

namespace Context.Memory.Tests;

public sealed class MemoryRelationJudgeTests
{
    [Fact]
    public void Parse_reads_one_verdict_per_neighbour_and_leaves_missing_ones_unrelated()
    {
        const string response = "Here are the verdicts:\n1|DUPLICATE|same retry count\r\n2 | complements | adds the timeout\n[3]|Supersede|newer value";

        var verdicts = MemoryRelationJudge.Parse(response, 4);

        verdicts.Should().Equal(
            new RelationVerdict(MemoryRelation.Duplicate, "same retry count"),
            new RelationVerdict(MemoryRelation.Complements, "adds the timeout"),
            new RelationVerdict(MemoryRelation.Supersedes, "newer value"),
            RelationVerdict.Unrelated);
    }

    [Fact]
    public void Parse_keeps_the_first_line_for_an_index()
    {
        MemoryRelationJudge.Parse("1|CONTRADICTS|a\n1|DUPLICATE|b", 1)
            .Should().Equal(new RelationVerdict(MemoryRelation.Contradicts, "a"));
    }

    [Fact]
    public void Parse_ignores_out_of_range_indexes_and_unknown_verdicts()
    {
        MemoryRelationJudge.Parse("0|DUPLICATE|x\n3|DUPLICATE|y\n2|MAYBE|z", 2)
            .Should().Equal(RelationVerdict.Unrelated, RelationVerdict.Unrelated);
    }

    [Theory]
    [InlineData("1|DUPLICATE")]
    [InlineData("1|DUPLICATE|   ")]
    public void Parse_treats_a_missing_reason_as_null(string response)
    {
        MemoryRelationJudge.Parse(response, 1).Should().Equal(new RelationVerdict(MemoryRelation.Duplicate, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I cannot tell.")]
    public void Parse_of_an_unreadable_response_is_all_unrelated(string? response)
    {
        MemoryRelationJudge.Parse(response, 2).Should().OnlyContain(v => v.Relation == MemoryRelation.Unrelated).And.HaveCount(2);
    }

    [Theory]
    [InlineData(0.99, true, true, true, true, MemoryRelation.Contradicts)]
    [InlineData(0.92, true, false, false, false, MemoryRelation.Duplicate)]
    [InlineData(0.95, false, true, true, false, MemoryRelation.Unrelated)]
    [InlineData(0.85, true, true, true, false, MemoryRelation.Complements)]
    [InlineData(0.85, true, false, true, false, MemoryRelation.Unrelated)]
    [InlineData(0.85, true, true, false, false, MemoryRelation.Unrelated)]
    [InlineData(0.79, true, true, true, false, MemoryRelation.Unrelated)]
    public void JudgeOffline_never_supersedes_and_needs_a_shared_entity(
        double cosine, bool sharesEntity, bool sameType, bool sameTopic, bool negation, MemoryRelation expected)
    {
        MemoryRelationJudge.JudgeOffline(new RelationSignals(cosine, sharesEntity, sameType, sameTopic, negation)).Should().Be(expected);
    }

    [Theory]
    [InlineData("Webhook retries are enabled", "Webhook retries are not enabled", true)]
    [InlineData("The client does retry webhooks", "The client doesn't retry webhooks", true)]
    [InlineData("Webhook retries are not enabled", "Webhook retries are never enabled", false)]
    [InlineData("It is not never cached", "It is cached", false)]
    [InlineData("Webhook retries are enabled", "GSI2 is not provisioned for billing", false)]
    public void HasNegationAsymmetry_compares_facts_with_negations_ignored(string left, string right, bool expected)
    {
        MemoryRelationJudge.HasNegationAsymmetry(Subject("Webhooks", left), Subject("Webhooks", right)).Should().Be(expected);
    }

    [Fact]
    public void HasNegationAsymmetry_uses_titles_only_when_a_side_has_no_facts()
    {
        MemoryRelationJudge.HasNegationAsymmetry(Subject("Retries enabled"), Subject("Retries not enabled")).Should().BeTrue();
        MemoryRelationJudge.HasNegationAsymmetry(
            Subject("Retries not enabled", "Timeout is 30 seconds"),
            Subject("Retries enabled", "Timeout is 30 seconds")).Should().BeFalse();
    }

    [Fact]
    public void BuildUserPrompt_numbers_the_neighbours_and_truncates_long_bodies()
    {
        var candidate = new RelationSubject(MemoryType.Decision, "Retry policy", ["Retries five times"], "Short body.");
        var neighbour = new RelationSubject(MemoryType.Fact, "Old retry policy", [], new string('x', MemoryRelationJudge.MaxBodyExcerpt + 50));

        var prompt = MemoryRelationJudge.BuildUserPrompt(candidate, [neighbour, neighbour]);

        prompt.Should().Contain("NEW").And.Contain("EXISTING 1").And.Contain("EXISTING 2");
        prompt.Should().Contain("TYPE: Decision").And.Contain("FACTS:").And.Contain("- Retries five times");
        prompt.Should().Contain(new string('x', MemoryRelationJudge.MaxBodyExcerpt) + "…");
        prompt.Should().NotContain(new string('x', MemoryRelationJudge.MaxBodyExcerpt + 1));
    }

    private static RelationSubject Subject(string title, params string[] facts) => new(MemoryType.Fact, title, facts, "Body.");
}
