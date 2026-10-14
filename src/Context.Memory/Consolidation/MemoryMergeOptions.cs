namespace Context.Memory.Consolidation;

/// <summary>
/// The per-project knobs of memory merging, read at call time so a settings change applies to the next
/// merge. <see cref="OnlineMaxSources"/> counts every memory combined by one online merge, the incoming
/// one included, so 2 is the pairwise merge. Out-of-range values are clamped by <see cref="Normalized"/>.
/// </summary>
public sealed record MemoryMergeOptions(
    bool OnlineMergeEnabled = true,
    int OnlineMaxSources = 5,
    int MaxBodyChars = 8000,
    int MaxFacts = 40,
    int ReconsolidationMaxGroupSize = 12,
    double ReconsolidationMinSimilarity = 0.60,
    int ReconsolidationSourceExcerptChars = 3000)
{
    public const int MinOnlineMaxSources = 2;
    public const int MaxOnlineMaxSources = 10;
    public const int MinBodyChars = 1000;
    public const int MaxBodyCharsLimit = 32000;
    public const int MinFacts = 5;
    public const int MaxFactsLimit = 100;
    public const int MinGroupSize = 2;
    public const int MaxGroupSizeLimit = 50;
    public const double MinSimilarity = 0.30;
    public const double MaxSimilarity = 0.95;
    public const int MinSourceExcerptChars = 500;
    public const int MaxSourceExcerptCharsLimit = 16000;

    public static readonly MemoryMergeOptions Default = new();

    public MemoryMergeOptions Normalized() => this with
    {
        OnlineMaxSources = Math.Clamp(OnlineMaxSources, MinOnlineMaxSources, MaxOnlineMaxSources),
        MaxBodyChars = Math.Clamp(MaxBodyChars, MinBodyChars, MaxBodyCharsLimit),
        MaxFacts = Math.Clamp(MaxFacts, MinFacts, MaxFactsLimit),
        ReconsolidationMaxGroupSize = Math.Clamp(ReconsolidationMaxGroupSize, MinGroupSize, MaxGroupSizeLimit),
        ReconsolidationMinSimilarity = double.IsNaN(ReconsolidationMinSimilarity)
            ? Default.ReconsolidationMinSimilarity
            : Math.Clamp(ReconsolidationMinSimilarity, MinSimilarity, MaxSimilarity),
        ReconsolidationSourceExcerptChars = Math.Clamp(ReconsolidationSourceExcerptChars, MinSourceExcerptChars, MaxSourceExcerptCharsLimit),
    };
}

/// <summary>Resolves a project's <see cref="MemoryMergeOptions"/>; Context.Application backs it with project settings.</summary>
public interface IMemoryMergeOptionsProvider
{
    Task<MemoryMergeOptions> GetAsync(Guid projectId, CancellationToken ct);
}
