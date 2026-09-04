using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Repositories;

public class UsageRecorder(ContextDbContext db) : IUsageRecorder
{
    public async Task RecordAsync(UsageRecord record, CancellationToken ct)
    {
        if (record.Id == Guid.Empty)
        {
            record.Id = Guid.NewGuid();
        }

        db.UsageRecords.Add(record);
        await db.SaveChangesAsync(ct);
    }

    public async Task<UsageSummary> GetSummaryAsync(Guid projectId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // Same SQLite/DateTimeOffset limitation noted on JobRepository.DequeueNextAsync and
        // SessionRepository's client-side sorts, but broader than originally assumed here: the
        // provider can't translate a >= / <= range comparison on a non-nullable DateTimeOffset
        // column at all (confirmed live -- InvalidOperationException, not just the GroupBy this
        // comment used to blame) -- only the ProjectId equality is pushed server-side; the date
        // range and all three aggregations run client-side with plain LINQ. One project's rows for
        // the requested window are a few thousand at most, so this stays cheap.
        var records = (await db.UsageRecords.AsNoTracking()
                .Where(u => u.ProjectId == projectId)
                .ToListAsync(ct))
            .Where(u => u.CreatedAt >= from && u.CreatedAt <= to)
            .ToList();

        var byPurpose = records
            .GroupBy(u => u.Purpose)
            .Select(g => new UsagePurposeSummary(g.Key, g.Sum(u => (long)(u.TotalTokens ?? 0)), g.LongCount()))
            .OrderByDescending(p => p.Tokens)
            .ToList();

        var byDay = records
            .GroupBy(u => DateOnly.FromDateTime(u.CreatedAt.UtcDateTime))
            .Select(g => new UsageDaySummary(g.Key, g.Sum(u => (long)(u.TotalTokens ?? 0)), g.LongCount()))
            .OrderBy(d => d.Day)
            .ToList();

        return new UsageSummary(
            records.Sum(u => (long)(u.TotalTokens ?? 0)),
            records.Count,
            byPurpose,
            byDay);
    }
}
