namespace Context.Infrastructure.Abstractions;

/// <summary>Aggregated LLM token-usage totals for a project over a date range (BACKLOG-27), as
/// returned by <see cref="IUsageRecorder.GetSummaryAsync"/>.</summary>
public sealed record UsageSummary(
    long TotalTokens,
    long TotalCalls,
    IReadOnlyList<UsagePurposeSummary> ByPurpose,
    IReadOnlyList<UsageDaySummary> ByDay);

/// <summary>Token/call totals for one <see cref="Context.Domain.Entities.UsageRecord.Purpose"/> value.</summary>
public sealed record UsagePurposeSummary(string Purpose, long Tokens, long Calls);

/// <summary>Token/call totals for one calendar day (UTC), for a Dashboard time series.</summary>
public sealed record UsageDaySummary(DateOnly Day, long Tokens, long Calls);
