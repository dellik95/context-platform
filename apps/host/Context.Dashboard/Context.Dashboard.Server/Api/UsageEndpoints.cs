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

public sealed record UsagePurposeDto(string Purpose, long Tokens, long Calls)
{
    public static UsagePurposeDto FromSummary(UsagePurposeSummary summary) => new(summary.Purpose, summary.Tokens, summary.Calls);
}

public sealed record UsageDayDto(DateOnly Day, long Tokens, long Calls)
{
    public static UsageDayDto FromSummary(UsageDaySummary summary) => new(summary.Day, summary.Tokens, summary.Calls);
}

public sealed record UsageSummaryDto(
    long TotalTokens,
    long TotalCalls,
    IReadOnlyList<UsagePurposeDto> ByPurpose,
    IReadOnlyList<UsageDayDto> ByDay)
{
    public static UsageSummaryDto FromSummary(UsageSummary summary) => new(
        summary.TotalTokens,
        summary.TotalCalls,
        [.. summary.ByPurpose.Select(UsagePurposeDto.FromSummary)],
        [.. summary.ByDay.Select(UsageDayDto.FromSummary)]);
}
