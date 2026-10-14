using Context.Application.Adapters;
using Context.Application.Settings;
using Context.Memory.Consolidation;
using FluentAssertions;
using Moq;

namespace Context.Application.Tests;

public sealed class MemoryMergeOptionsAdapterTests
{
    private readonly Mock<IProjectSettingsService> _settings = new();
    private readonly Guid _projectId = Guid.NewGuid();

    private ProjectSettingsDto Settings() => new(
        EmbeddingProvider: "noop", LlmProvider: "noop", OllamaBaseUrl: null, OllamaEmbeddingModel: null,
        OllamaChatModel: null, ReindexEnabled: false, ReindexIntervalMinutes: 60, LastReindexScheduledAt: null);

    [Fact]
    public async Task GetAsync_maps_the_project_merge_settings()
    {
        _settings.Setup(s => s.GetAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(Settings() with
        {
            OnlineMergeEnabled = false,
            OnlineMergeMaxSources = 7,
            MergedMemoryMaxBodyChars = 12000,
            MergedMemoryMaxFacts = 60,
            ReconsolidationMaxGroupSize = 20,
            ReconsolidationClusterMinSimilarity = 0.7,
            ReconsolidationSourceExcerptChars = 4000,
        });

        var options = await new MemoryMergeOptionsAdapter(_settings.Object).GetAsync(_projectId, CancellationToken.None);

        options.Should().Be(new MemoryMergeOptions(false, 7, 12000, 60, 20, 0.7, 4000));
    }

    [Fact]
    public async Task GetAsync_clamps_hand_edited_values_into_their_valid_range()
    {
        _settings.Setup(s => s.GetAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(Settings() with
        {
            OnlineMergeMaxSources = 0,
            MergedMemoryMaxBodyChars = int.MaxValue,
            MergedMemoryMaxFacts = -3,
            ReconsolidationMaxGroupSize = 1,
            ReconsolidationClusterMinSimilarity = 3,
            ReconsolidationSourceExcerptChars = 1,
        });

        var options = await new MemoryMergeOptionsAdapter(_settings.Object).GetAsync(_projectId, CancellationToken.None);

        options.Should().Be(new MemoryMergeOptions(
            true, MemoryMergeOptions.MinOnlineMaxSources, MemoryMergeOptions.MaxBodyCharsLimit, MemoryMergeOptions.MinFacts,
            MemoryMergeOptions.MinGroupSize, MemoryMergeOptions.MaxSimilarity, MemoryMergeOptions.MinSourceExcerptChars));
    }

    [Fact]
    public void Periodic_reconsolidation_is_on_every_six_hours_by_default()
    {
        Settings().MemoryReconsolidationIntervalMinutes.Should().Be(360);
    }
}
