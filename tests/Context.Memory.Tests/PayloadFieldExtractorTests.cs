using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Summarization;
using FluentAssertions;

namespace Context.Memory.Tests;

public class PayloadFieldExtractorTests
{
    [Fact]
    public void ExtractDistinctPaths_returns_distinct_sorted_paths_for_matching_type()
    {
        var events = new List<SessionEvent>
        {
            Evt(SessionEventType.FileRead, """{"path":"b.cs"}"""),
            Evt(SessionEventType.FileRead, """{"path":"a.cs"}"""),
            Evt(SessionEventType.FileRead, """{"path":"a.cs"}"""),
        };

        var result = PayloadFieldExtractor.ExtractDistinctPaths(events, SessionEventType.FileRead);

        result.Should().Equal("a.cs", "b.cs");
    }

    [Fact]
    public void ExtractDistinctPaths_ignores_events_of_other_types()
    {
        var events = new List<SessionEvent>
        {
            Evt(SessionEventType.FileRead, """{"path":"read.cs"}"""),
            Evt(SessionEventType.FileEdit, """{"path":"edit.cs"}"""),
        };

        var result = PayloadFieldExtractor.ExtractDistinctPaths(events, SessionEventType.FileEdit);

        result.Should().ContainSingle().Which.Should().Be("edit.cs");
    }

    [Fact]
    public void ExtractDistinctPaths_skips_events_with_unextractable_payloads()
    {
        var events = new List<SessionEvent>
        {
            Evt(SessionEventType.FileRead, "not json"),
            Evt(SessionEventType.FileRead, """{"unrelated":"field"}"""),
            Evt(SessionEventType.FileRead, """{"path":"ok.cs"}"""),
        };

        var result = PayloadFieldExtractor.ExtractDistinctPaths(events, SessionEventType.FileRead);

        result.Should().ContainSingle().Which.Should().Be("ok.cs");
    }

    private static SessionEvent Evt(SessionEventType type, string payloadJson) => new()
    {
        Id = Guid.NewGuid(),
        SessionId = Guid.NewGuid(),
        Type = type,
        PayloadJson = payloadJson,
        Timestamp = DateTimeOffset.UtcNow
    };
}
