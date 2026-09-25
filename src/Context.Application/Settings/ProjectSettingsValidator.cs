namespace Context.Application.Settings;

/// <summary>
/// Floor-validates the numeric knobs that would otherwise silently misbehave rather than error:
/// <see cref="ProjectSettingsDto.MinEventsForMemoryExtraction"/> at 0 would claim an extraction
/// batch (one LLM call) on every single recorded event, and the other
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

        if (dto.MemoryConsolidationSimilarityThreshold <= 0 || dto.MemoryConsolidationSimilarityThreshold > 1)
        {
            errors.Add($"{nameof(dto.MemoryConsolidationSimilarityThreshold)} must be in (0, 1].");
        }

        if (dto.MemoryConsolidationLowSimilarityThreshold <= 0 || dto.MemoryConsolidationLowSimilarityThreshold > 1)
        {
            errors.Add($"{nameof(dto.MemoryConsolidationLowSimilarityThreshold)} must be in (0, 1].");
        }

        if (dto.MemoryConsolidationLowSimilarityThreshold > dto.MemoryConsolidationSimilarityThreshold)
        {
            errors.Add($"{nameof(dto.MemoryConsolidationLowSimilarityThreshold)} must be less than or equal to {nameof(dto.MemoryConsolidationSimilarityThreshold)}.");
        }

        if (dto.MemoryConsolidationPromotionReinforcementThreshold < 1)
        {
            errors.Add($"{nameof(dto.MemoryConsolidationPromotionReinforcementThreshold)} must be at least 1.");
        }

        if (dto.MemoryConsolidationTopKForDedup < 1)
        {
            errors.Add($"{nameof(dto.MemoryConsolidationTopKForDedup)} must be at least 1.");
        }

        return errors;
    }
}
