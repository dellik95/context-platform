using Context.Domain.Enums;
using Context.Memory.Search;
using FluentAssertions;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Memory.Tests;

public sealed class MemorySearchTests
{
    private static DomainMemory Memory(string title, string body, MemoryType type = MemoryType.Decision) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Guid.NewGuid(),
        Type = type,
        Title = title,
        Body = body,
        Confidence = 0.7,
    };

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("what changed recently?")]
    [InlineData("what's still pending")]
    [InlineData("how does it work")]
    public void Parse_finds_no_subject_in_blank_or_intent_only_queries(string query) =>
        MemoryQuery.Parse(query).HasSubject.Should().BeFalse();

    [Fact]
    public void Parse_keeps_subject_terms_and_strong_entity_keys()
    {
        var query = MemoryQuery.Parse("  How does PHUN-42 change the TokenRefreshService?  ");

        query.Text.Should().Be("How does PHUN-42 change the TokenRefreshService?");
        query.Terms.Should().Contain(["phun", "tokenrefreshservice"]).And.NotContain(["how", "does", "change", "the"]);
        query.EntityKeys.Should().Contain("ticket:phun-42");
        query.HasSubject.Should().BeTrue();
    }

    [Fact]
    public void LexicalMatch_is_one_for_a_whole_phrase_hit_and_a_term_fraction_otherwise()
    {
        var memory = Memory("Token refresh strategy", "Refresh tokens rotate on every use");
        memory.FactsJson = "[\"Rotation happens in AuthRotator\"]";

        MemoryQuery.Parse("token refresh").LexicalMatch(memory).Should().Be(1);
        MemoryQuery.Parse("refresh rotation budget").LexicalMatch(memory).Should().BeApproximately(2.0 / 3, 1e-9);
        MemoryQuery.Parse("authrotator").LexicalMatch(memory).Should().Be(1);
        MemoryQuery.Parse("dashboard").LexicalMatch(memory).Should().Be(0);
        MemoryQuery.Parse("what changed").LexicalMatch(memory).Should().Be(0);
    }

    [Fact]
    public void LexicalMatch_matches_terms_that_prefix_each_other_from_four_characters()
    {
        var memory = Memory("Migrations", "EF Core migration runs on startup");

        MemoryQuery.Parse("migrate startups").LexicalMatch(memory).Should().Be(0.5);
        MemoryQuery.Parse("migrations startup").LexicalMatch(memory).Should().Be(1);
    }

    [Fact]
    public void Header_and_Format_render_the_canonical_note()
    {
        var memory = Memory("Cache TTL", "Five minutes");
        memory.Topic = " Caching ";
        memory.Confidence = 0.8567;
        memory.SourceCount = 3;
        memory.Status = MemoryStatus.Contested;
        memory.FactsJson = "[\"Set in CacheOptions\",\"\",\"Applies to reads\"]";

        MemoryNote.Header(memory).Should().Be("[Decision · Caching · conf 0.86 · 3 sources · CONTESTED]");
        MemoryNote.Format(memory).Should().Be("[Decision · Caching · conf 0.86 · 3 sources · CONTESTED] Cache TTL: Five minutes");
        MemoryNote.Format(memory, includeFacts: true)
            .Should().Be("[Decision · Caching · conf 0.86 · 3 sources · CONTESTED] Cache TTL: Five minutes Facts: Set in CacheOptions; Applies to reads");
    }

    [Fact]
    public void Header_omits_a_blank_topic_and_uses_singular_source()
    {
        var memory = Memory("Nightly job", "Ran reindex", MemoryType.Event);

        MemoryNote.Header(memory).Should().Be("[Event · conf 0.70 · 1 source]");
        MemoryNote.Format(memory, includeFacts: true).Should().Be("[Event · conf 0.70 · 1 source] Nightly job: Ran reindex");
    }
}
