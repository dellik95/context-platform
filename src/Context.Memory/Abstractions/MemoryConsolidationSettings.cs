namespace Context.Memory.Abstractions;

/// <summary>The four knobs <see cref="IMemoryConsolidator"/> uses to decide reinforce vs.
/// supersede vs. insert-as-new. Defaults match the values these were hardcoded to before becoming
/// per-project settings (see <c>Context.Application.Settings.ProjectSettingsDto</c>'s
/// MemoryConsolidation* fields, the actual source of truth).</summary>
public sealed record MemoryConsolidationSettings(
    double SimilarityThreshold = 0.92,
    double LowSimilarityThreshold = 0.80,
    int PromotionReinforcementThreshold = 2,
    int TopKForDedup = 3);
