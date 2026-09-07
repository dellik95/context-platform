namespace Context.Infrastructure.Abstractions;

/// <summary>
/// Aggregated token totals for a project over a date range (BACKLOG-27), as returned by
/// <see cref="IUsageRecorder.GetSummaryAsync"/>. <see cref="TotalTokens"/>/<see cref="TotalCalls"/>
/// are real spend from LLM calls; <see cref="TotalSavedTokens"/> is the estimated (counterfactual)
/// savings from MCP search-tool calls avoiding full-file reads - see
/// Context.Domain.Entities.UsageRecord.SavedTokens for the exact methodology. The two are never
/// mixed within one <see cref="UsageRecord"/> row, but both are summed here so the Dashboard can
/// show spend and savings side by side.
/// </summary>
public sealed record UsageSummary(
    long TotalTokens,
    long TotalCalls,
    long TotalSavedTokens,
    IReadOnlyList<UsagePurposeSummary> ByPurpose,
    IReadOnlyList<UsageDaySummary> ByDay);

/// <summary>Token/call totals for one <see cref="Context.Domain.Entities.UsageRecord.Purpose"/> value.</summary>
public sealed record UsagePurposeSummary(string Purpose, long Tokens, long Calls, long SavedTokens);

/// <summary>Token/call totals for one calendar day (UTC), for a Dashboard time series.</summary>
public sealed record UsageDaySummary(DateOnly Day, long Tokens, long Calls, long SavedTokens);
