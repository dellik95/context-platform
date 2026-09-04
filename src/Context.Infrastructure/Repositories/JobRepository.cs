using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Repositories;

public class JobRepository(ContextDbContext db) : IJobRepository
{
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
        // SQLite's EF Core provider cannot translate ORDER BY over DateTimeOffset columns
        // (CreatedAt), so candidates are filtered server-side and ordered client-side.
        var candidates = await db.Jobs
            .Where(j => j.Status == JobStatus.Pending && (j.NextRetryAt == null || j.NextRetryAt <= now))
            .ToListAsync(ct);

        var job = candidates
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

    public async Task<JobStatusCounts> CountByStatusAsync(CancellationToken ct)
    {
        var counts = await db.Jobs.AsNoTracking()
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
}
