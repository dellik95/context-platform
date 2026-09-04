using Context.Domain.Entities;
using Context.Memory.Extraction;
using FluentAssertions;

namespace Context.Memory.Tests;

public class MemoryExtractionPromptBuilderTests
{
    [Fact]
    public void BuildUserPrompt_includes_Files_read_section_with_paths_when_present()
    {
        var chunk = new SessionSummaryChunk { SessionId = Guid.NewGuid(), FilesReadJson = """["src/Foo.cs"]""" };

        var prompt = MemoryExtractionPromptBuilder.BuildUserPrompt(chunk);

        prompt.Should().Contain("Files read:").And.Contain("src/Foo.cs");
    }

    [Fact]
    public void BuildUserPrompt_shows_none_placeholder_when_FilesReadJson_is_null()
    {
        var chunk = new SessionSummaryChunk { SessionId = Guid.NewGuid() };

        var prompt = MemoryExtractionPromptBuilder.BuildUserPrompt(chunk);

        prompt.Should().Contain($"Files read:{Environment.NewLine}(none)");
    }

    [Fact]
    public void BuildUserPrompt_still_includes_Changed_files_section()
    {
        var chunk = new SessionSummaryChunk { SessionId = Guid.NewGuid(), ChangedFilesJson = """["src/Bar.cs"]""" };

        var prompt = MemoryExtractionPromptBuilder.BuildUserPrompt(chunk);

        prompt.Should().Contain("Changed files:").And.Contain("src/Bar.cs");
    }
}
