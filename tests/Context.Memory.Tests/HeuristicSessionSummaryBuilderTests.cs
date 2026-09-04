using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Summarization;
using FluentAssertions;

namespace Context.Memory.Tests;

public class HeuristicSessionSummaryBuilderTests
{
    [Fact]
    public void Build_populates_FilesReadJson_from_FileRead_events()
    {
        var events = new List<SessionEvent>
        {
            Evt(SessionEventType.FileRead, """{"path":"src/Foo.cs"}"""),
            Evt(SessionEventType.FileRead, """{"path":"src/Bar.cs"}"""),
        };

        var summary = HeuristicSessionSummaryBuilder.Build(Guid.NewGuid(), events, DateTimeOffset.UtcNow);

        var read = JsonSerializer.Deserialize<List<string>>(summary.FilesReadJson!);
        read.Should().Equal("src/Bar.cs", "src/Foo.cs");
    }

    [Fact]
    public void Build_does_not_stuff_read_files_into_Discovered()
    {
        var events = new List<SessionEvent> { Evt(SessionEventType.FileRead, """{"path":"src/Foo.cs"}""") };

        var summary = HeuristicSessionSummaryBuilder.Build(Guid.NewGuid(), events, DateTimeOffset.UtcNow);

        summary.Discovered.Should().BeNull();
        summary.Completed.Should().Contain("file(s) read for context");
    }

    [Fact]
    public void Build_still_populates_ChangedFilesJson_from_FileEdit_events()
    {
        var events = new List<SessionEvent> { Evt(SessionEventType.FileEdit, """{"path":"src/Foo.cs"}""") };

        var summary = HeuristicSessionSummaryBuilder.Build(Guid.NewGuid(), events, DateTimeOffset.UtcNow);

        var edited = JsonSerializer.Deserialize<List<string>>(summary.ChangedFilesJson!);
        edited.Should().Equal("src/Foo.cs");
    }

    [Fact]
    public void Build_serializes_empty_arrays_not_null_when_no_files_touched()
    {
        var events = new List<SessionEvent> { Evt(SessionEventType.Prompt, "{}") };

        var summary = HeuristicSessionSummaryBuilder.Build(Guid.NewGuid(), events, DateTimeOffset.UtcNow);

        summary.FilesReadJson.Should().Be("[]");
        summary.ChangedFilesJson.Should().Be("[]");
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
