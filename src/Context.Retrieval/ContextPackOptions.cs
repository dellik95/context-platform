namespace Context.Retrieval;

/// <summary>
/// Token-budget section reserves for <see cref="ContextPackBuilder"/>, per spec §9.7/§9.8:
/// ~10% architecture, ~45% primary files/symbols, ~25% historical/memory notes, ~10% open
/// issues, ~10% held back as a safety reserve. Ratios sum to 1.0.
/// </summary>
public sealed class ContextPackOptions
{
    public double ArchitectureReserveRatio { get; set; } = ContextPackBuilder.DefaultArchitectureReserveRatio;
    public double PrimaryReserveRatio { get; set; } = ContextPackBuilder.DefaultPrimaryReserveRatio;
    public double HistoricalReserveRatio { get; set; } = ContextPackBuilder.DefaultHistoricalReserveRatio;
    public double OpenIssuesReserveRatio { get; set; } = ContextPackBuilder.DefaultOpenIssuesReserveRatio;
    public double SafetyReserveRatio { get; set; } = ContextPackBuilder.DefaultSafetyReserveRatio;
}
