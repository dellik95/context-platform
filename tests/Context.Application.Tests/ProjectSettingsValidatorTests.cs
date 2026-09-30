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
