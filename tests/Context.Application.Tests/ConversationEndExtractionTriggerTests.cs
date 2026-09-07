using Context.Application.Memory.Triggers;
using Context.Application.Settings;
using FluentAssertions;

namespace Context.Application.Tests;

/// <summary>Tests for <see cref="ConversationEndExtractionTrigger"/> in isolation - the "session-end"
/// arm of <see cref="Jobs.Handlers.SummarizeSessionJobHandler"/>'s extraction-trigger check.</summary>
public sealed class ConversationEndExtractionTriggerTests
{
    private readonly ConversationEndExtractionTrigger _sut = new();

    private static ProjectSettingsDto MakeSettings() => new(
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
        MinEventsForMemoryExtraction: 20);

    [Fact]
    public void Name_is_ConversationEnd()
    {
        _sut.Name.Should().Be("ConversationEnd");
    }

    [Fact]
    public void ShouldFire_is_true_when_the_session_is_ending()
    {
        var context = new MemoryExtractionTriggerContext(IsSessionEnd: true, ChunkEventCount: 0, MakeSettings());

        _sut.ShouldFire(context).Should().BeTrue();
    }

    [Fact]
    public void ShouldFire_is_false_when_the_session_is_not_ending()
    {
        // Zero event-count gate here on purpose: this trigger must judge session-end alone,
        // otherwise SummarizeSessionJobHandler's OR-of-triggers would double-fire with
        // EventThresholdExtractionTrigger.
        var context = new MemoryExtractionTriggerContext(IsSessionEnd: false, ChunkEventCount: 1000, MakeSettings());

        _sut.ShouldFire(context).Should().BeFalse();
    }
}
