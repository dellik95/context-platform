using Context.Domain.Entities;

namespace Context.Infrastructure.Abstractions;

/// <summary>Persists LLM token usage for cost accounting (BACKLOG-27).</summary>
public interface IUsageRecorder
{
    Task RecordAsync(UsageRecord record, CancellationToken ct);

    /// <summary>Total tokens/calls for <paramref name="projectId"/> within
    /// [<paramref name="from"/>, <paramref name="to"/>], plus the same totals broken down by
    /// <see cref="UsageRecord.Purpose"/> and by calendar day, for the Dashboard's usage view.</summary>
    Task<UsageSummary> GetSummaryAsync(Guid projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
}
