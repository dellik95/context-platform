using Context.Providers.Embeddings;

namespace Context.Application.Settings;

/// <summary>
/// Floor-validates the numeric knobs that would otherwise silently misbehave rather than error:
/// <see cref="ProjectSettingsDto.MinEventsForMemoryExtraction"/> at 0 would claim an extraction
/// batch (one LLM call) on every single recorded event, and the other
/// two follow the same "count/size, not a sentinel" shape. <see cref="ProjectSettingsDto.SessionSummaryIntervalMinutes"/>,
/// <see cref="ProjectSettingsDto.StaleSessionTimeoutMinutes"/> and
/// <see cref="ProjectSettingsDto.MemoryReconsolidationIntervalMinutes"/> are deliberately excluded -
/// <c>&lt;= 0</c> is their documented "disabled" sentinel, not an invalid value.
/// </summary>
public static class ProjectSettingsValidator
{
    public static IReadOnlyList<string> Validate(ProjectSettingsDto dto)
    {
        var errors = new List<string>();

        if (dto.MinEventsForSessionSummary < 1)
        {
            errors.Add($"{nameof(dto.MinEventsForSessionSummary)} must be at least 1.");
        }

        if (dto.MinEventsForMemoryExtraction < 1)
        {
            errors.Add($"{nameof(dto.MinEventsForMemoryExtraction)} must be at least 1.");
        }

        if (dto.MaxMemoryNotesPerSection < 1)
        {
            errors.Add($"{nameof(dto.MaxMemoryNotesPerSection)} must be at least 1.");
        }

        if (dto.ReindexIntervalMinutes < 1)
        {
            errors.Add($"{nameof(dto.ReindexIntervalMinutes)} must be at least 1.");
        }

        if (dto.LlmTimeoutSeconds < 1)
        {
            errors.Add($"{nameof(dto.LlmTimeoutSeconds)} must be at least 1.");
        }

        if (dto.LlmMaxRetries < 0)
        {
            errors.Add($"{nameof(dto.LlmMaxRetries)} must not be negative.");
        }

        if (dto.LlmRetryBaseDelaySeconds < 1)
        {
            errors.Add($"{nameof(dto.LlmRetryBaseDelaySeconds)} must be at least 1.");
        }

        if (dto.RateLimitCooldownMinutes < 1)
        {
            errors.Add($"{nameof(dto.RateLimitCooldownMinutes)} must be at least 1.");
        }

        if (dto.RateLimitMaxCooldownMinutes < dto.RateLimitCooldownMinutes)
        {
            errors.Add($"{nameof(dto.RateLimitMaxCooldownMinutes)} must be at least {nameof(dto.RateLimitCooldownMinutes)}.");
        }

        if (dto.EmbeddingBatchSize is < 0 or > EmbeddingBatchLimits.MaxConfigurableInputs)
        {
            errors.Add($"{nameof(dto.EmbeddingBatchSize)} must be between 0 (provider default) and {EmbeddingBatchLimits.MaxConfigurableInputs}.");
        }

        return errors;
    }
}
