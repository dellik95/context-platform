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

    public async Task<Job?> DequeueNextAsync(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var now = DateTimeOffset.UtcNow;
        // SQLite's EF Core provider cannot translate comparisons or ORDER BY over DateTimeOffset
        // columns (NextRetryAt, CreatedAt) -- attempting the `NextRetryAt <= now` comparison
        // server-side throws InvalidOperationException ("could not be translated") at execution
        // time, not at compile time, so this was never caught until DequeueNextAsync was actually
        // exercised. Status is a plain enum comparison and Priority a plain int (both safe
        // server-side); the backoff check and the CreatedAt tie-break both move to client-side,
        // over just the top DequeueBatchSize by priority rather than every Pending row (see that
        // constant's remarks).
        var candidates = await db.Jobs
            .Where(j => j.Status == JobStatus.Pending)
            .OrderByDescending(j => j.Priority)
            .Take(DequeueBatchSize)
            .ToListAsync(ct);

        var job = candidates
            .Where(j => j.NextRetryAt == null || j.NextRetryAt <= now)
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.CreatedAt)
            .FirstOrDefault();

        if (job is null)
        {
            await tx.CommitAsync(ct);
            return null;
        }

        job.Status = JobStatus.Running;
        job.StartedAt = now;
        job.Attempts += 1;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return job;
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

    public Task<int> DeleteByStatusAsync(JobStatus status, Guid? projectId, CancellationToken ct) =>
        db.Jobs
            .Where(j => j.Status == status && (projectId == null || j.ProjectId == projectId))
            .ExecuteDeleteAsync(ct);
}
