using Context.Application.Settings;
using Context.Memory.Abstractions;

namespace Context.Application.Adapters;

/// <summary>Adapts Context.Memory's <see cref="IMemoryConsolidationSettingsProvider"/> seam onto
/// the real <see cref="IProjectSettingsService"/>, same pattern as <see cref="UsageRecorderAdapter"/>.</summary>
public sealed class MemoryConsolidationSettingsProviderAdapter(IProjectSettingsService settingsService) : IMemoryConsolidationSettingsProvider
{
    public async Task<MemoryConsolidationSettings> GetAsync(Guid projectId, CancellationToken ct)
    {
        var dto = await settingsService.GetAsync(projectId, ct).ConfigureAwait(false);
        return new MemoryConsolidationSettings(
            dto.MemoryConsolidationSimilarityThreshold,
            dto.MemoryConsolidationLowSimilarityThreshold,
            dto.MemoryConsolidationPromotionReinforcementThreshold,
            dto.MemoryConsolidationTopKForDedup);
    }
}
