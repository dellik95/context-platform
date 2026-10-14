using Context.Application.Settings;
using FluentAssertions;
using Xunit;

namespace Context.Application.Tests;

public sealed class ProjectSettingsValidatorTests
{
    [Fact]
    public void Validate_returns_no_errors_for_defaults()
    {
        var errors = ProjectSettingsValidator.Validate(BuildValidSettings());

        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_rejects_non_positive_MinEventsForSessionSummary(int value)
    {
        var dto = BuildValidSettings() with { MinEventsForSessionSummary = value };

        var errors = ProjectSettingsValidator.Validate(dto);

        errors.Should().ContainSingle(e => e.Contains(nameof(ProjectSettingsDto.MinEventsForSessionSummary)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_rejects_non_positive_MinEventsForMemoryExtraction(int value)
    {
        var dto = BuildValidSettings() with { MinEventsForMemoryExtraction = value };

        var errors = ProjectSettingsValidator.Validate(dto);

        errors.Should().ContainSingle(e => e.Contains(nameof(ProjectSettingsDto.MinEventsForMemoryExtraction)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_rejects_non_positive_MaxMemoryNotesPerSection(int value)
    {
        var dto = BuildValidSettings() with { MaxMemoryNotesPerSection = value };

        var errors = ProjectSettingsValidator.Validate(dto);

        errors.Should().ContainSingle(e => e.Contains(nameof(ProjectSettingsDto.MaxMemoryNotesPerSection)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_rejects_non_positive_ReindexIntervalMinutes(int value)
    {
        var dto = BuildValidSettings() with { ReindexIntervalMinutes = value };

        var errors = ProjectSettingsValidator.Validate(dto);

        errors.Should().ContainSingle(e => e.Contains(nameof(ProjectSettingsDto.ReindexIntervalMinutes)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_allows_non_positive_SessionSummaryIntervalMinutes_as_disabled_sentinel(int value)
    {
        var dto = BuildValidSettings() with { SessionSummaryIntervalMinutes = value };

        var errors = ProjectSettingsValidator.Validate(dto);

        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_allows_non_positive_StaleSessionTimeoutMinutes_as_disabled_sentinel(int value)
    {
        var dto = BuildValidSettings() with { StaleSessionTimeoutMinutes = value };

        var errors = ProjectSettingsValidator.Validate(dto);

        errors.Should().BeEmpty();
    }

    [Fact]
    public void Validate_rejects_a_zero_LlmTimeoutSeconds()
    {
        var errors = ProjectSettingsValidator.Validate(BuildValidSettings() with { LlmTimeoutSeconds = 0 });

        errors.Should().ContainSingle(e => e.Contains(nameof(ProjectSettingsDto.LlmTimeoutSeconds)));
    }

    [Fact]
    public void Validate_allows_zero_LlmMaxRetries_but_not_negative()
    {
        ProjectSettingsValidator.Validate(BuildValidSettings() with { LlmMaxRetries = 0 }).Should().BeEmpty();
        ProjectSettingsValidator.Validate(BuildValidSettings() with { LlmMaxRetries = -1 })
            .Should().ContainSingle(e => e.Contains(nameof(ProjectSettingsDto.LlmMaxRetries)));
    }

    [Fact]
    public void Validate_rejects_a_max_cooldown_below_the_initial_one()
    {
        var dto = BuildValidSettings() with { RateLimitCooldownMinutes = 10, RateLimitMaxCooldownMinutes = 5 };

        var errors = ProjectSettingsValidator.Validate(dto);

        errors.Should().ContainSingle(e => e.Contains(nameof(ProjectSettingsDto.RateLimitMaxCooldownMinutes)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(256)]
    public void Validate_allows_an_EmbeddingBatchSize_from_provider_default_to_the_cap(int value)
    {
        ProjectSettingsValidator.Validate(BuildValidSettings() with { EmbeddingBatchSize = value }).Should().BeEmpty();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(257)]
    public void Validate_rejects_an_EmbeddingBatchSize_outside_0_to_256(int value)
    {
        ProjectSettingsValidator.Validate(BuildValidSettings() with { EmbeddingBatchSize = value })
            .Should().ContainSingle(e => e.Contains(nameof(ProjectSettingsDto.EmbeddingBatchSize)));
    }

    [Fact]
    public void Validate_accepts_the_default_merge_settings()
    {
        ProjectSettingsValidator.Validate(BuildValidSettings()).Should().BeEmpty();
    }

    [Theory]
    [InlineData(nameof(ProjectSettingsDto.OnlineMergeMaxSources), 1, true)]
    [InlineData(nameof(ProjectSettingsDto.OnlineMergeMaxSources), 2, false)]
    [InlineData(nameof(ProjectSettingsDto.OnlineMergeMaxSources), 10, false)]
    [InlineData(nameof(ProjectSettingsDto.OnlineMergeMaxSources), 11, true)]
    [InlineData(nameof(ProjectSettingsDto.MergedMemoryMaxBodyChars), 999, true)]
    [InlineData(nameof(ProjectSettingsDto.MergedMemoryMaxBodyChars), 1000, false)]
    [InlineData(nameof(ProjectSettingsDto.MergedMemoryMaxBodyChars), 32000, false)]
    [InlineData(nameof(ProjectSettingsDto.MergedMemoryMaxBodyChars), 32001, true)]
    [InlineData(nameof(ProjectSettingsDto.MergedMemoryMaxFacts), 4, true)]
    [InlineData(nameof(ProjectSettingsDto.MergedMemoryMaxFacts), 5, false)]
    [InlineData(nameof(ProjectSettingsDto.MergedMemoryMaxFacts), 100, false)]
    [InlineData(nameof(ProjectSettingsDto.MergedMemoryMaxFacts), 101, true)]
    [InlineData(nameof(ProjectSettingsDto.ReconsolidationMaxGroupSize), 1, true)]
    [InlineData(nameof(ProjectSettingsDto.ReconsolidationMaxGroupSize), 2, false)]
    [InlineData(nameof(ProjectSettingsDto.ReconsolidationMaxGroupSize), 50, false)]
    [InlineData(nameof(ProjectSettingsDto.ReconsolidationMaxGroupSize), 51, true)]
    [InlineData(nameof(ProjectSettingsDto.ReconsolidationSourceExcerptChars), 499, true)]
    [InlineData(nameof(ProjectSettingsDto.ReconsolidationSourceExcerptChars), 500, false)]
    [InlineData(nameof(ProjectSettingsDto.ReconsolidationSourceExcerptChars), 16000, false)]
    [InlineData(nameof(ProjectSettingsDto.ReconsolidationSourceExcerptChars), 16001, true)]
    public void Validate_enforces_the_merge_setting_ranges(string field, int value, bool rejected)
    {
        var settings = field switch
        {
            nameof(ProjectSettingsDto.OnlineMergeMaxSources) => BuildValidSettings() with { OnlineMergeMaxSources = value },
            nameof(ProjectSettingsDto.MergedMemoryMaxBodyChars) => BuildValidSettings() with { MergedMemoryMaxBodyChars = value },
            nameof(ProjectSettingsDto.MergedMemoryMaxFacts) => BuildValidSettings() with { MergedMemoryMaxFacts = value },
            nameof(ProjectSettingsDto.ReconsolidationMaxGroupSize) => BuildValidSettings() with { ReconsolidationMaxGroupSize = value },
            _ => BuildValidSettings() with { ReconsolidationSourceExcerptChars = value },
        };

        var errors = ProjectSettingsValidator.Validate(settings);

        if (rejected)
        {
            errors.Should().ContainSingle(e => e.Contains(field));
        }
        else
        {
            errors.Should().BeEmpty();
        }
    }

    [Theory]
    [InlineData(0.29, true)]
    [InlineData(0.30, false)]
    [InlineData(0.95, false)]
    [InlineData(0.96, true)]
    [InlineData(double.NaN, true)]
    public void Validate_enforces_the_cluster_similarity_range(double value, bool rejected)
    {
        var errors = ProjectSettingsValidator.Validate(BuildValidSettings() with { ReconsolidationClusterMinSimilarity = value });

        errors.Should().HaveCount(rejected ? 1 : 0);
    }

    private static ProjectSettingsDto BuildValidSettings() =>
        new(
            EmbeddingProvider: "noop",
            LlmProvider: "noop",
            OllamaBaseUrl: null,
            OllamaEmbeddingModel: null,
            OllamaChatModel: null,
            ReindexEnabled: false,
            ReindexIntervalMinutes: 60,
            LastReindexScheduledAt: null);
}
