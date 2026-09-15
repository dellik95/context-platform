namespace Context.Memory.Abstractions;

/// <summary>Seam letting <see cref="IMemoryConsolidator"/> read its per-project thresholds
/// without depending on Context.Application's <c>IProjectSettingsService</c> directly (which in
/// turn depends on Context.Memory — see Context.Application.Adapters for the real
/// implementation).</summary>
public interface IMemoryConsolidationSettingsProvider
{
    Task<MemoryConsolidationSettings> GetAsync(Guid projectId, CancellationToken ct);
}
