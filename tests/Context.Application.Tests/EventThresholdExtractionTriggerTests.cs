using Context.Application.Memory.Triggers;
using Context.Application.Settings;
using FluentAssertions;

namespace Context.Application.Tests;

/// <summary>Tests for <see cref="EventThresholdExtractionTrigger"/> in isolation - the "N events"
/// arm of <see cref="Jobs.Handlers.SummarizeSessionJobHandler"/>'s extraction-trigger check.</summary>
public sealed class EventThresholdExtractionTriggerTests
{
    private readonly EventThresholdExtractionTrigger _sut = new();

    private static ProjectSettingsDto MakeSettings(int minEventsForMemoryExtraction) => new(
        EmbeddingProvider: "noop",
        LlmProvider: "noop",
        OllamaBaseUrl: null,
        OllamaEmbeddingModel: null,
        OllamaChatModel: null,
        ReindexEnabled: false,
        ReindexIntervalMinutes: 60,
        LastReindexScheduledAt: null,
        SessionSummaryIntervalMinutes: 0,
        MinEventsForSessionSummary: 15,
        MaxMemoryNotesPerSection: 20,
        MinEventsForMemoryExtraction: minEventsForMemoryExtraction);

    [Fact]
    public void Name_is_EventThreshold()
    {
        _sut.Name.Should().Be("EventThreshold");
    }

    [Fact]
    public void ShouldFire_is_false_when_the_chunk_event_count_is_below_the_threshold()
    {
        var context = new MemoryExtractionTriggerContext(IsSessionEnd: false, ChunkEventCount: 19, MakeSettings(20));

        _sut.ShouldFire(context).Should().BeFalse();
    }

    [Fact]
    public void ShouldFire_is_true_when_the_chunk_event_count_meets_the_threshold()
    {
        var context = new MemoryExtractionTriggerContext(IsSessionEnd: false, ChunkEventCount: 20, MakeSettings(20));

        _sut.ShouldFire(context).Should().BeTrue();
    }

    [Fact]
    public void ShouldFire_is_true_when_the_chunk_event_count_exceeds_the_threshold()
    {
        var context = new MemoryExtractionTriggerContext(IsSessionEnd: false, ChunkEventCount: 21, MakeSettings(20));

        _sut.ShouldFire(context).Should().BeTrue();
    }

    [Fact]
    public void ShouldFire_ignores_IsSessionEnd_entirely()
    {
        // ConversationEndExtractionTrigger owns the session-end arm - this trigger must judge the
        // event count alone, otherwise SummarizeSessionJobHandler's OR-of-triggers would double-fire.
        var context = new MemoryExtractionTriggerContext(IsSessionEnd: true, ChunkEventCount: 0, MakeSettings(20));

        _sut.ShouldFire(context).Should().BeFalse();
    }
}
