using Context.Memory.Consolidation;

namespace Context.Application.Tests;

internal sealed class FixedMergeOptionsProvider(MemoryMergeOptions? options = null) : IMemoryMergeOptionsProvider
{
    public Task<MemoryMergeOptions> GetAsync(Guid projectId, CancellationToken ct) => Task.FromResult(options ?? MemoryMergeOptions.Default);
}
