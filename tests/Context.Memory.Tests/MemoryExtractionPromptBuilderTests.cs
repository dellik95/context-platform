using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Extraction;
using FluentAssertions;

namespace Context.Memory.Tests;

public class MemoryExtractionPromptBuilderTests
{
    [Fact]
    public void BuildUserPromptFromEvents_renders_each_event_with_its_tool_input_and_file_lists()
    {
        var sessionId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 9, 25, 10, 15, 30, TimeSpan.Zero);
        SessionEvent[] events =
        [
            new() { SessionId = sessionId, Type = SessionEventType.FileRead, Timestamp = at, PayloadJson = """{"toolName":"Read","path":"src/Foo.cs"}""" },
            new() { SessionId = sessionId, Type = SessionEventType.FileEdit, Timestamp = at, PayloadJson = """{"toolName":"Edit","path":"src/Bar.cs","toolInput":"rename Baz"}""" },
        ];

        var prompt = MemoryExtractionPromptBuilder.BuildUserPromptFromEvents(sessionId, events);

        prompt.Should().Contain("[10:15:30] FileRead Read src/Foo.cs")
            .And.Contain("input: rename Baz")
            .And.Contain($"Files read:{Environment.NewLine}- src/Foo.cs")
            .And.Contain($"Changed files:{Environment.NewLine}- src/Bar.cs");
    }

    [Fact]
    public void BuildUserPromptFromEvents_truncates_oversized_tool_output()
    {
        var sessionId = Guid.NewGuid();
        var output = new string('x', 5000);
        SessionEvent[] events =
        [
            new() { SessionId = sessionId, Type = SessionEventType.ToolCall, Timestamp = DateTimeOffset.UtcNow, PayloadJson = $$"""{"toolName":"Bash","toolResponse":"{{output}}"}""" },
        ];

        var prompt = MemoryExtractionPromptBuilder.BuildUserPromptFromEvents(sessionId, events);

        prompt.Should().Contain("output: ").And.Contain("…").And.NotContain(output);
    }

    [Fact]
    public void BuildUserPromptFromEvents_includes_the_assistant_reply_text()
    {
        var sessionId = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 9, 25, 10, 15, 30, TimeSpan.Zero);
        SessionEvent[] events =
        [
            new() { SessionId = sessionId, Type = SessionEventType.AssistantMessage, Timestamp = at, PayloadJson = """{"message":"Root cause: the retry policy swallowed 429s."}""" },
        ];

        var prompt = MemoryExtractionPromptBuilder.BuildUserPromptFromEvents(sessionId, events);

        prompt.Should().Contain("[10:15:30] AssistantMessage")
            .And.Contain("  Root cause: the retry policy swallowed 429s.")
            .And.NotContain("message:");
    }

    [Fact]
    public void BuildUserPromptFromCompactSummary_includes_the_summary_text()
    {
        var prompt = MemoryExtractionPromptBuilder.BuildUserPromptFromCompactSummary(Guid.NewGuid(), "  Chose Polly over hand-rolled retries.  ");

        prompt.Should().Contain("compacted").And.Contain("Chose Polly over hand-rolled retries.");
    }
}
