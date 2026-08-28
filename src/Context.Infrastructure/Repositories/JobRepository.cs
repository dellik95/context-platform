using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Repositories;

public class JobRepository(ContextDbContext db) : IJobRepository
{
    public Task<Job?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);

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
        var job = await db.Jobs
            .Where(j => j.Status == JobStatus.Pending && (j.NextRetryAt == null || j.NextRetryAt <= now))
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

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

    public async Task<IReadOnlyList<Job>> ListByStatusAsync(JobStatus status, CancellationToken ct) =>
        await db.Jobs.AsNoTracking()
            .Where(j => j.Status == status)
            .OrderByDescending(j => j.Priority)
            .ThenBy(j => j.CreatedAt)
            .ToListAsync(ct);
}
