using Context.Application.Settings;
using Context.Memory.Consolidation;

namespace Context.Application.Adapters;

/// <summary>Backs <see cref="IMemoryMergeOptionsProvider"/> with the project's merge settings, clamped to their valid ranges.</summary>
public sealed class MemoryMergeOptionsAdapter(IProjectSettingsService settingsService) : IMemoryMergeOptionsProvider
{
    public async Task<MemoryMergeOptions> GetAsync(Guid projectId, CancellationToken ct) =>
        (await settingsService.GetAsync(projectId, ct).ConfigureAwait(false)).ToMergeOptions();
}
