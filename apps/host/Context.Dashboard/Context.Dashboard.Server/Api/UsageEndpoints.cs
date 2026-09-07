using Context.Dashboard.Server.Security;
using Context.Infrastructure.Abstractions;

namespace Context.Dashboard.Server.Api;

/// <summary>
/// <c>GET /api/usage</c> — aggregated LLM token-usage totals for the Dashboard's usage view
/// (BACKLOG-27). Read-only over what <see cref="Context.Infrastructure.Repositories.UsageRecorder"/>
/// already records — no new tracking is introduced here.
/// </summary>
public static class UsageEndpoints
{
    private const int DefaultDays = 30;

    public static void MapUsageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/usage", GetSummaryAsync).RequireBearerToken();
    }

    private static async Task<IResult> GetSummaryAsync(
        Guid projectId, int? days, IUsageRecorder usage, CancellationToken ct)
    {
        var effectiveDays = days is > 0 ? days.Value : DefaultDays;
        var to = DateTimeOffset.UtcNow;
        var from = to.AddDays(-effectiveDays);

        var summary = await usage.GetSummaryAsync(projectId, from, to, ct).ConfigureAwait(false);
        return Results.Ok(UsageSummaryDto.FromSummary(summary));
    }
}

/// <summary><see cref="EstimatedSavedTokens"/> is a counterfactual estimate (see
/// <see cref="Context.Domain.Entities.UsageRecord.SavedTokens"/>), not a measured spend figure like
/// <see cref="Tokens"/> - the Dashboard must not present the two as the same kind of number.</summary>
public sealed record UsagePurposeDto(string Purpose, long Tokens, long Calls, long EstimatedSavedTokens)
{
    public static UsagePurposeDto FromSummary(UsagePurposeSummary summary) =>
        new(summary.Purpose, summary.Tokens, summary.Calls, summary.SavedTokens);
}

/// <summary><see cref="EstimatedSavedTokens"/> is a counterfactual estimate, not a measured spend
/// figure like <see cref="Tokens"/> - see <see cref="UsagePurposeDto"/>.</summary>
public sealed record UsageDayDto(DateOnly Day, long Tokens, long Calls, long EstimatedSavedTokens)
{
    public static UsageDayDto FromSummary(UsageDaySummary summary) =>
        new(summary.Day, summary.Tokens, summary.Calls, summary.SavedTokens);
}

/// <summary>
/// <see cref="TotalTokens"/>/<see cref="TotalCalls"/> are real LLM-call spend.
/// <see cref="TotalEstimatedSavedTokens"/> is the estimated (counterfactual) tokens saved by using
/// this platform's MCP search tools instead of reading whole files - see
/// <see cref="Context.Domain.Entities.UsageRecord.SavedTokens"/> for the exact methodology.
/// <see cref="EstimatedNetTokens"/> is simply <c>TotalEstimatedSavedTokens - TotalTokens</c>, offered
/// as a convenience "profit" figure; it mixes a real number with an estimate, so the Dashboard
/// should label it accordingly rather than presenting it as audited.
/// </summary>
public sealed record UsageSummaryDto(
    long TotalTokens,
    long TotalCalls,
    long TotalEstimatedSavedTokens,
    long EstimatedNetTokens,
    IReadOnlyList<UsagePurposeDto> ByPurpose,
    IReadOnlyList<UsageDayDto> ByDay)
{
    public static UsageSummaryDto FromSummary(UsageSummary summary) => new(
        summary.TotalTokens,
        summary.TotalCalls,
        summary.TotalSavedTokens,
        summary.TotalSavedTokens - summary.TotalTokens,
        [.. summary.ByPurpose.Select(UsagePurposeDto.FromSummary)],
        [.. summary.ByDay.Select(UsageDayDto.FromSummary)]);
}
