using Context.Memory.Summarization;
using FluentAssertions;

namespace Context.Memory.Tests;

public class SessionSummaryParserTests
{
    [Fact]
    public void Parse_response_with_only_Decisions_filled_is_not_effectively_empty()
    {
        var parsed = SessionSummaryParser.Parse("DECISIONS:\n- picked option A over B\n");

        parsed.IsEffectivelyEmpty.Should().BeFalse();
        parsed.Decisions.Should().Contain("picked option A over B");
    }

    [Fact]
    public void Parse_response_with_all_sections_none_is_effectively_empty()
    {
        var parsed = SessionSummaryParser.Parse(
            "TITLE:\n(none)\nCOMPLETED:\n(none)\nDISCOVERED:\n(none)\nDECISIONS:\n(none)\nOPEN_ISSUES:\n(none)\nNEXT_STEPS:\n(none)\n");

        parsed.IsEffectivelyEmpty.Should().BeTrue();
    }
}
