namespace Context.Application.Settings;

/// <summary>
/// Floor-validates the numeric knobs that would otherwise silently misbehave rather than error:
/// <see cref="ProjectSettingsDto.MinEventsForMemoryExtraction"/> at 0 makes
/// <c>EventThresholdExtractionTrigger</c> fire on every chunk regardless of size, and the other
/// two follow the same "count/size, not a sentinel" shape. <see cref="ProjectSettingsDto.SessionSummaryIntervalMinutes"/>,
/// <see cref="ProjectSettingsDto.StaleSessionTimeoutMinutes"/> and
/// <see cref="ProjectSettingsDto.MemoryCompactionIntervalMinutes"/> are deliberately excluded -
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

        if (dto.MemoryCompactionSimilarityThreshold <= 0 || dto.MemoryCompactionSimilarityThreshold > 1)
        {
            errors.Add($"{nameof(dto.MemoryCompactionSimilarityThreshold)} must be in (0, 1].");
        }

        return errors;
    }
}
