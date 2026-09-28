using Context.Domain.Enums;
using Context.Memory.Consolidation;
using FluentAssertions;

namespace Context.Memory.Tests;

public sealed class ReconsolidationPromptTests
{
    [Fact]
    public void Parses_canonicals_contradictions_and_keep_separate()
    {
        const string response = """
            CANONICAL
            TYPE: Behavior
            TITLE: Checkout sessions refresh their amount on reuse
            TOPIC: Checkout session refresh
            BODY:
            CheckoutSessionV1Manager refreshes a reused session's amount.
            FACTS:
            - On true, upsert updates session amount to balance token value
            - Expired sessions are re-armed +15 minutes.
            ENTITIES: CheckoutSessionV1Manager, IsAmountRefreshRequired
            FILES_READ:
            - src/CheckoutSessionV1Manager.cs
            SOURCES: 1, 3
            END_CANONICAL

            CONTRADICTIONS:
            - 2|4|one says LogWarning, the other LogInformation

            KEEP_SEPARATE:
            - 5
            """;

        var parsed = ReconsolidationPrompt.Parse(response, 5);

        parsed.Problems.Should().BeEmpty();
        var canonical = parsed.Canonicals.Should().ContainSingle().Subject;
        canonical.Sources.Should().Equal(1, 3);
        canonical.Candidate.Type.Should().Be(MemoryType.Behavior);
        canonical.Candidate.Facts.Should().HaveCount(2);
        canonical.Candidate.Entities.Should().Equal("CheckoutSessionV1Manager", "IsAmountRefreshRequired");
        canonical.Candidate.FilesRead.Should().Equal("src/CheckoutSessionV1Manager.cs");
        parsed.Contradictions.Should().Equal(new ParsedContradiction(2, 4, "one says LogWarning, the other LogInformation"));
        parsed.KeepSeparate.Should().Equal(5);
    }

    [Fact]
    public void None_sections_parse_to_an_empty_plan()
    {
        var parsed = ReconsolidationPrompt.Parse("CONTRADICTIONS: none\nKEEP_SEPARATE: none", 3);

        parsed.Problems.Should().BeEmpty();
        parsed.Canonicals.Should().BeEmpty();
        parsed.Contradictions.Should().BeEmpty();
        parsed.KeepSeparate.Should().BeEmpty();
    }

    [Theory]
    [InlineData("CANONICAL\nTYPE: Fact\nTITLE: t\nBODY: b\nSOURCES: 1, 4\nEND_CANONICAL\nCONTRADICTIONS: none\nKEEP_SEPARATE: none", "source 4 does not exist")]
    [InlineData("CANONICAL\nBODY: no type or title\nSOURCES: 1, 2\nEND_CANONICAL\nCONTRADICTIONS: none\nKEEP_SEPARATE: none", "canonical 1 is malformed")]
    [InlineData("CANONICAL\nTYPE: Fact\nTITLE: t\nBODY: b\nEND_CANONICAL\nCONTRADICTIONS: none\nKEEP_SEPARATE: none", "canonical 1 names no sources")]
    [InlineData("CONTRADICTIONS:\n- 2|2|itself\nKEEP_SEPARATE: none", "source 2 contradicts itself")]
    [InlineData("I think these memories are all related.", "the response has no recognizable sections")]
    public void Reports_what_makes_an_answer_unusable(string response, string problem)
    {
        ReconsolidationPrompt.Parse(response, 3).Problems.Should().Contain(problem);
    }

    [Fact]
    public void User_prompt_numbers_sources_from_one()
    {
        var memories = new[]
        {
            new Context.Domain.Entities.Memory { Type = MemoryType.Fact, Title = "First", Body = "first body", FactsJson = "[\"a fact\"]" },
            new Context.Domain.Entities.Memory { Type = MemoryType.Issue, Title = "Second", Body = "second body", FilesReadJson = "[\"src/A.cs\"]" },
        };

        var prompt = ReconsolidationPrompt.BuildUserPrompt(memories).Replace("\r\n", "\n");

        prompt.Should().Contain("SOURCE 1\nTYPE: Fact\nTITLE: First").And.Contain("- a fact").And.Contain("SOURCE 2\nTYPE: Issue\nTITLE: Second")
            .And.Contain("FILES: src/A.cs");
    }
}
