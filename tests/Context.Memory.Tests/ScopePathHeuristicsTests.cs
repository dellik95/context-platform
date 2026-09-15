using Context.Memory.Extraction;
using FluentAssertions;

namespace Context.Memory.Tests;

public class ScopePathHeuristicsTests
{
    [Fact]
    public void DeriveFromFiles_returns_the_shared_directory_of_several_modified_files()
    {
        var result = ScopePathHeuristics.DeriveFromFiles(
            filesModified: ["src/Context.Application/Jobs/JobDispatcher.cs", "src/Context.Application/Jobs/JobRetryPolicy.cs"],
            filesRead: []);

        result.Should().Be("src/Context.Application/Jobs");
    }

    [Fact]
    public void DeriveFromFiles_returns_the_containing_directory_of_a_single_file()
    {
        var result = ScopePathHeuristics.DeriveFromFiles(
            filesModified: ["src/Context.Memory/Extraction/MemoryConsolidator.cs"],
            filesRead: []);

        result.Should().Be("src/Context.Memory/Extraction");
    }

    [Fact]
    public void DeriveFromFiles_falls_back_to_FilesRead_when_FilesModified_is_empty()
    {
        var result = ScopePathHeuristics.DeriveFromFiles(
            filesModified: [],
            filesRead: ["src/Context.Providers/Embeddings/OllamaEmbeddingGeneratorDecorator.cs"]);

        result.Should().Be("src/Context.Providers/Embeddings");
    }

    [Fact]
    public void DeriveFromFiles_returns_null_when_files_share_no_common_directory()
    {
        var result = ScopePathHeuristics.DeriveFromFiles(
            filesModified: ["docs/README.md", "src/Context.Domain/Entities/Memory.cs"],
            filesRead: []);

        result.Should().BeNull();
    }

    [Fact]
    public void DeriveFromFiles_returns_null_when_there_are_no_files_at_all()
    {
        var result = ScopePathHeuristics.DeriveFromFiles(filesModified: [], filesRead: []);

        result.Should().BeNull();
    }

    [Fact]
    public void DeriveFromFiles_prefers_FilesModified_over_FilesRead_when_both_are_present()
    {
        var result = ScopePathHeuristics.DeriveFromFiles(
            filesModified: ["src/Context.Application/Jobs/JobDispatcher.cs"],
            filesRead: ["src/Context.Domain/Entities/Memory.cs"]);

        result.Should().Be("src/Context.Application/Jobs");
    }
}
