using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Tests;

/// <summary>
/// Claim and retention behavior of <see cref="JobRepository"/>, against a real on-disk SQLite file
/// (same pattern as <see cref="MemoryRepositoryTests"/>) — an in-memory provider would not exercise
/// the conditional UPDATE these tests are about.
/// </summary>
public sealed class JobRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-jobrepo-tests-{Guid.NewGuid():N}.db");

    private ContextDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ContextDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        return new ContextDbContext(optionsBuilder.Options);
    }

    private static Job NewJob(int priority = 0, JobStatus status = JobStatus.Pending, DateTimeOffset? createdAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = JobType.EmbedChunk,
        PayloadJson = "{}",
        Status = status,
        Priority = priority,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
    };

    private async Task MigrateAsync()
    {
        using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    [Fact]
    public async Task DequeueNextAsync_claims_the_highest_priority_job_and_marks_it_running()
    {
        await MigrateAsync();
        using var db = CreateContext();

        var low = NewJob(priority: 1);
        var high = NewJob(priority: 9);
        db.Jobs.AddRange(low, high);
        await db.SaveChangesAsync();

        var claimed = await new JobRepository(db).DequeueNextAsync(CancellationToken.None);

        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(high.Id);
        claimed.Status.Should().Be(JobStatus.Running);
        claimed.Attempts.Should().Be(1);
        claimed.StartedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DequeueNextAsync_never_hands_the_same_job_to_two_concurrent_claimers()
    {
        await MigrateAsync();

        using (var seed = CreateContext())
        {
            seed.Jobs.Add(NewJob());
            await seed.SaveChangesAsync();
        }

        // Separate DbContexts, as two dispatcher processes sharing one database file would have.
        using var dbA = CreateContext();
        using var dbB = CreateContext();

        var first = await new JobRepository(dbA).DequeueNextAsync(CancellationToken.None);
        var second = await new JobRepository(dbB).DequeueNextAsync(CancellationToken.None);

        // The conditional UPDATE is what decides this: with a plain read-then-write both claimers
        // would see one Pending row and both run it.
        first.Should().NotBeNull();
        second.Should().BeNull("the row was no longer Pending when the second claimer's UPDATE ran");
    }

    [Fact]
    public async Task DequeueNextAsync_skips_a_job_whose_retry_backoff_has_not_elapsed()
    {
        await MigrateAsync();
        using var db = CreateContext();

        var backingOff = NewJob(priority: 9);
        backingOff.NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(5);
        var ready = NewJob(priority: 1);
        db.Jobs.AddRange(backingOff, ready);
        await db.SaveChangesAsync();

        var claimed = await new JobRepository(db).DequeueNextAsync(CancellationToken.None);

        claimed.Should().NotBeNull();
        claimed!.Id.Should().Be(ready.Id, "priority does not override a backoff that has not elapsed");
    }

    [Fact]
    public async Task DeleteFinishedOlderThanAsync_removes_only_terminal_jobs_past_the_cutoff()
    {
        await MigrateAsync();
        using var db = CreateContext();

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-7);

        var oldDone = NewJob(status: JobStatus.Done);
        oldDone.FinishedAt = now.AddDays(-30);
        var oldFailed = NewJob(status: JobStatus.Failed);
        oldFailed.FinishedAt = now.AddDays(-30);
        var recentDone = NewJob(status: JobStatus.Done);
        recentDone.FinishedAt = now.AddHours(-1);
        var oldPending = NewJob(createdAt: now.AddDays(-30));
        var oldRunning = NewJob(status: JobStatus.Running, createdAt: now.AddDays(-30));

        db.Jobs.AddRange(oldDone, oldFailed, recentDone, oldPending, oldRunning);
        await db.SaveChangesAsync();

        var deleted = await new JobRepository(db).DeleteFinishedOlderThanAsync(cutoff, CancellationToken.None);

        deleted.Should().Be(2);
        var survivors = await db.Jobs.AsNoTracking().Select(j => j.Id).ToListAsync();
        survivors.Should().BeEquivalentTo([recentDone.Id, oldPending.Id, oldRunning.Id],
            "a still-queued or in-flight job is not retention's business no matter how old it is");
    }

    [Fact]
    public async Task DeleteFinishedOlderThanAsync_ages_out_a_terminal_job_that_never_recorded_FinishedAt()
    {
        await MigrateAsync();
        using var db = CreateContext();

        var orphan = NewJob(status: JobStatus.Done, createdAt: DateTimeOffset.UtcNow.AddDays(-30));
        orphan.FinishedAt = null;
        db.Jobs.Add(orphan);
        await db.SaveChangesAsync();

        var deleted = await new JobRepository(db).DeleteFinishedOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-7), CancellationToken.None);

        deleted.Should().Be(1, "falling back to CreatedAt stops such a row pinning itself in the table forever");
    }

    [Fact]
    public async Task GetLastEnqueuedAtAsync_returns_the_newest_job_of_that_type_regardless_of_status()
    {
        await MigrateAsync();
        using var db = CreateContext();

        var newest = DateTimeOffset.UtcNow.AddHours(-1);
        var older = NewJob(status: JobStatus.Done, createdAt: DateTimeOffset.UtcNow.AddDays(-3));
        var newer = NewJob(status: JobStatus.Failed, createdAt: newest);
        var otherType = NewJob(createdAt: DateTimeOffset.UtcNow);
        otherType.Type = JobType.BackupDb;

        db.Jobs.AddRange(older, newer, otherType);
        await db.SaveChangesAsync();

        var last = await new JobRepository(db).GetLastEnqueuedAtAsync(JobType.EmbedChunk, CancellationToken.None);

        last.Should().BeCloseTo(newest, TimeSpan.FromSeconds(1),
            "a Failed run still proves the scheduler already fired for that interval");
    }

    [Fact]
    public async Task GetLastEnqueuedAtAsync_returns_null_when_the_type_has_never_been_enqueued()
    {
        await MigrateAsync();
        using var db = CreateContext();

        var last = await new JobRepository(db).GetLastEnqueuedAtAsync(JobType.BackupDb, CancellationToken.None);

        last.Should().BeNull();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }
}
