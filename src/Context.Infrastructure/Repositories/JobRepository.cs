using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Repositories;

public class JobRepository(ContextDbContext db) : IJobRepository
{
    /// <summary>
    /// Caps how many Pending rows <see cref="DequeueNextAsync"/> materializes per call. Without
    /// this, a large backlog (e.g. thousands of EmbedChunk jobs queued at once) meant pulling every
    /// single Pending row -- PayloadJson included -- into memory on every claim, an O(n) cost paid
    /// once per job claimed, i.e. O(n^2) overall; observed turning a startup that should take
    /// seconds into a multi-minute stall that outran systemd's start timeout and crash-looped the
    /// whole host. The batch is still ordered by Priority server-side (a plain int, unlike
    /// NextRetryAt/CreatedAt -- see the comment below), so a bounded batch only risks claiming
    /// slightly out of exact CreatedAt order among jobs that share the same priority under a very
    /// large backlog -- never skipping or losing a job, since every remaining Pending row is still
    /// found by the next call once this batch is claimed or filtered out.
    /// </summary>
    private const int DequeueBatchSize = 200;


    public Task<Job?> GetByIdAsync(Guid id, CancellationToken ct, Guid? projectId = null) =>
        db.Jobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == id && (projectId == null || j.ProjectId == projectId || j.ProjectId == null), ct);

    public async Task<Job> EnqueueAsync(Job job, CancellationToken ct)
    {
        if (job.Id == Guid.Empty)
        {
            job.Id = Guid.NewGuid();
        }

        db.Jobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job;
    }

    public async Task UpdateAsync(Job job, CancellationToken ct)
    {
        db.Jobs.Update(job);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The candidate-selection projection. Selecting whole <see cref="Job"/> rows here would pull
    /// <see cref="Job.PayloadJson"/> for up to <see cref="DequeueBatchSize"/> jobs on every single
    /// claim, and the payload is the one column that can be large (a whole file's parsed relation
    /// drafts, for instance) — it is only needed for the one job actually won.
    /// </summary>
    private sealed record ClaimCandidate(Guid Id, int Priority, DateTimeOffset CreatedAt, DateTimeOffset? NextRetryAt);

    public Task<Job?> DequeueNextAsync(CancellationToken ct) =>
        DequeueNextCoreAsync(db.Jobs.AsNoTracking().Where(j => j.Status == JobStatus.Pending), ct);

    public Task<Job?> DequeueNextForProjectAsync(Guid? projectId, CancellationToken ct) =>
        DequeueNextCoreAsync(
            db.Jobs.AsNoTracking().Where(j => j.Status == JobStatus.Pending && j.ProjectId == projectId), ct);

    public async Task<Job?> ForceClaimAsync(Guid id, JobStatus expectedStatus, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var claimed = await db.Jobs
            .Where(j => j.Id == id && j.Status == expectedStatus)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(j => j.Status, JobStatus.Running)
                    .SetProperty(j => j.StartedAt, now)
                    .SetProperty(j => j.Attempts, 1)
                    .SetProperty(j => j.LastError, (string?)null)
                    .SetProperty(j => j.NextRetryAt, (DateTimeOffset?)null)
                    .SetProperty(j => j.FinishedAt, (DateTimeOffset?)null),
                ct);

        return claimed == 0 ? null : await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
    }

    public async Task<IReadOnlyList<Guid?>> ListProjectsWithPendingJobsAsync(CancellationToken ct) =>
        await db.Jobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Pending)
            .Select(j => j.ProjectId)
            .Distinct()
            .ToListAsync(ct);

    private async Task<Job?> DequeueNextCoreAsync(IQueryable<Job> pending, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // SQLite's EF Core provider cannot translate comparisons or ORDER BY over DateTimeOffset
        // columns (NextRetryAt, CreatedAt) -- attempting the `NextRetryAt <= now` comparison
        // server-side throws InvalidOperationException ("could not be translated") at execution
        // time, not at compile time, so this was never caught until DequeueNextAsync was actually
        // exercised. Status is a plain enum comparison and Priority a plain int (both safe
        // server-side); the backoff check and the CreatedAt tie-break both move to client-side,
        // over just the top DequeueBatchSize by priority rather than every Pending row (see that
        // constant's remarks).
        var candidates = await pending
            .OrderByDescending(j => j.Priority)
            .Take(DequeueBatchSize)
            .Select(j => new ClaimCandidate(j.Id, j.Priority, j.CreatedAt, j.NextRetryAt))
            .ToListAsync(ct);

        var runnable = candidates
            .Where(c => c.NextRetryAt == null || c.NextRetryAt <= now)
            .OrderByDescending(c => c.Priority)
            .ThenBy(c => c.CreatedAt);

        foreach (var candidate in runnable)
        {
            // Conditional UPDATE ... WHERE Status = Pending, and the affected-row count decides the
            // race. A read-then-SaveChanges inside a transaction did NOT: SQLite opens a DEFERRED
            // transaction, so the SELECT takes only a shared read lock and a second dispatcher —
            // another thread here, or the second host process this design explicitly supports via
            // CONTEXT_PLATFORM_REDIS — could read the same Pending row and write Running over it,
            // running one job twice. Losing the race costs one no-op UPDATE and moves to the next
            // candidate, so a contended queue still drains at full rate.
            var claimed = await db.Jobs
                .Where(j => j.Id == candidate.Id && j.Status == JobStatus.Pending)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(j => j.Status, JobStatus.Running)
                        .SetProperty(j => j.StartedAt, now)
                        .SetProperty(j => j.Attempts, j => j.Attempts + 1),
                    ct);

            if (claimed == 0)
            {
                continue;
            }

            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == candidate.Id, ct);
            if (job is not null)
            {
                return job;
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, Guid? projectId, CancellationToken ct)
    {
        var jobs = await db.Jobs.AsNoTracking()
            .Where(j => j.Status == status && (projectId == null || j.ProjectId == projectId))
            .ToListAsync(ct);

        return [.. jobs.OrderByDescending(j => j.Priority).ThenBy(j => j.CreatedAt)];
    }

    public async Task<JobStatusCounts> CountByStatusAsync(Guid projectId, CancellationToken ct)
    {
        var counts = await db.Jobs.AsNoTracking()
            .Where(j => j.ProjectId == projectId)
            .GroupBy(j => j.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int CountFor(JobStatus status) => counts.Find(c => c.Key == status)?.Count ?? 0;

        return new JobStatusCounts(
            CountFor(JobStatus.Pending),
            CountFor(JobStatus.Running),
            CountFor(JobStatus.Failed),
            CountFor(JobStatus.Done));
    }

    /// <summary>Keeps one retention sweep's DELETE statements off SQLite's parameter limit.</summary>
    private const int RetentionDeleteBatchSize = 500;

    public async Task<int> DeleteFinishedOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct)
    {
        // Only the two columns the cutoff needs are materialized. The DateTimeOffset comparison has
        // to run client-side (same provider limitation DequeueNextAsync documents), and Jobs is
        // precisely the table that reaches six figures, so pulling whole entities — PayloadJson
        // included — just to decide what to delete would cost more than the rows being freed.
        var terminal = await db.Jobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Done || j.Status == JobStatus.Failed)
            .Select(j => new { j.Id, j.FinishedAt, j.CreatedAt })
            .ToListAsync(ct);

        // FinishedAt should always be set on a terminal job; falling back to CreatedAt means a row
        // that somehow missed it still ages out instead of pinning itself in the table forever.
        var expired = terminal
            .Where(j => (j.FinishedAt ?? j.CreatedAt) < cutoff)
            .Select(j => j.Id)
            .ToList();

        var deleted = 0;
        foreach (var batch in expired.Chunk(RetentionDeleteBatchSize))
        {
            deleted += await db.Jobs.Where(j => batch.Contains(j.Id)).ExecuteDeleteAsync(ct);
        }

        return deleted;
    }

    public async Task<DateTimeOffset?> GetLastEnqueuedAtAsync(JobType type, CancellationToken ct)
    {
        // Max()/OrderBy over a DateTimeOffset column is untranslatable on SQLite, so the aggregate
        // runs client-side over just that one column, filtered by the (Type, Status) index.
        var createdAt = await db.Jobs.AsNoTracking()
            .Where(j => j.Type == type)
            .Select(j => j.CreatedAt)
            .ToListAsync(ct);

        return createdAt.Count == 0 ? null : createdAt.Max();
    }

    public Task<int> DeleteByStatusAsync(JobStatus status, Guid? projectId, CancellationToken ct) =>
        db.Jobs
            .Where(j => j.Status == status && (projectId == null || j.ProjectId == projectId))
            .ExecuteDeleteAsync(ct);
}
