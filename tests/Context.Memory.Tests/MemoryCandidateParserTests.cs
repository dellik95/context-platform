using Context.Domain.Enums;
using Context.Memory.Extraction;
using FluentAssertions;

namespace Context.Memory.Tests;

public class MemoryCandidateParserTests
{
    [Fact]
    public void Parse_reads_the_TOPIC_line_when_present()
    {
        const string response = """
            MEMORY_COUNT: 1
            MEMORY 1
            TYPE: Decision
            TITLE: Switched to trigram FTS5 tokenizer
            BODY: chunks_fts now uses tokenize='trigram' so PascalCase identifiers match substring queries.
            TOPIC: Search indexing
            CONFIDENCE: 0.9
            END_MEMORY
            """;

        var candidates = MemoryCandidateParser.Parse(response);

        candidates.Should().ContainSingle();
        candidates[0].Topic.Should().Be("Search indexing");
    }

    [Fact]
    public void Parse_leaves_Topic_null_when_the_TOPIC_line_is_omitted()
    {
        const string response = """
            MEMORY_COUNT: 1
            MEMORY 1
            TYPE: Discovery
            TITLE: Something worth remembering
            BODY: A concrete, specific body sentence.
            CONFIDENCE: 0.8
            END_MEMORY
            """;

        var candidates = MemoryCandidateParser.Parse(response);

        candidates.Should().ContainSingle();
        candidates[0].Topic.Should().BeNull();
    }

    [Fact]
    public void Parse_gives_each_memory_in_a_multi_memory_response_its_own_TOPIC()
    {
        const string response = """
            MEMORY_COUNT: 2
            MEMORY 1
            TYPE: Decision
            TITLE: First decision
            BODY: First body.
            TOPIC: Job dispatcher retry logic
            CONFIDENCE: 0.85
            END_MEMORY
            MEMORY 2
            TYPE: BugFix
            TITLE: Second fix
            BODY: Second body.
            TOPIC: Embedding pipeline concurrency
            CONFIDENCE: 0.75
            END_MEMORY
            """;

        var candidates = MemoryCandidateParser.Parse(response);

        candidates.Should().HaveCount(2);
        candidates[0].Type.Should().Be(MemoryType.Decision);
        candidates[0].Topic.Should().Be("Job dispatcher retry logic");
        candidates[1].Type.Should().Be(MemoryType.BugFix);
        candidates[1].Topic.Should().Be("Embedding pipeline concurrency");
    }
}
