using Context.Domain.Enums;
using Context.Memory.Consolidation;
using FluentAssertions;

namespace Context.Memory.Tests;

public sealed class MemoryMergePromptTests
{
    [Fact]
    public void User_prompt_lists_every_body_in_order_under_the_first_title()
    {
        List<RelationSubject> subjects =
        [
            new(MemoryType.Fact, "First title", [], "first body"),
            new(MemoryType.Fact, "Second title", [], "second body"),
            new(MemoryType.Fact, "Third title", [], "third body"),
        ];

        var prompt = MemoryMergePrompt.BuildUserPrompt(subjects, ["a fact"], 5000).Replace("\r\n", "\n");

        prompt.Should().StartWith("TITLE: First title\n")
            .And.Contain("under 5000 characters").And.Contain("- a fact")
            .And.Contain("BODY 1:\nfirst body").And.Contain("BODY 2:\nsecond body").And.Contain("BODY 3:\nthird body");
    }

    [Fact]
    public void Clean_strips_a_body_prefix_but_never_cuts_the_text()
    {
        var long_ = new string('x', 50_000);

        MemoryMergePrompt.Clean($"BODY: {long_}").Should().Be(long_);
        MemoryMergePrompt.Clean("   ").Should().BeNull();
        MemoryMergePrompt.Clean(null).Should().BeNull();
    }

    [Fact]
    public void Fallback_keeps_every_body_and_drops_one_the_text_so_far_already_contains()
    {
        MemoryMergePrompt.Fallback(["First body.", "Second body.", "first   BODY."]).Should().Be("First body.\n\nSecond body.");
    }

    [Fact]
    public void Fallback_never_truncates()
    {
        var bodies = Enumerable.Range(0, 5).Select(i => $"Body {i} " + new string((char)('a' + i), 3000)).ToList();

        MemoryMergePrompt.Fallback(bodies).Length.Should().Be(bodies.Sum(b => b.Length) + 2 * 4);
    }
}
