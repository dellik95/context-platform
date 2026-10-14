using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
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

    private static Job NewJob(
        int priority = 0, JobStatus status = JobStatus.Pending, DateTimeOffset? createdAt = null, JobType type = JobType.EmbedChunk) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
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
    public async Task ForceClaimAsync_claims_a_backing_off_job_as_a_fresh_attempt()
    {
        await MigrateAsync();
        using var db = CreateContext();

        var job = NewJob();
        job.Attempts = 4;
        job.LastError = "System.Net.Http.HttpRequestException: Connection refused";
        job.NextRetryAt = DateTimeOffset.UtcNow.AddMinutes(5);
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        var claimed = await new JobRepository(db).ForceClaimAsync(job.Id, JobStatus.Pending, CancellationToken.None);

        claimed.Should().NotBeNull();
        claimed!.Status.Should().Be(JobStatus.Running);
        claimed.Attempts.Should().Be(1);
        claimed.LastError.Should().BeNull();
        claimed.NextRetryAt.Should().BeNull();
    }

    [Fact]
    public async Task ForceClaimAsync_returns_null_when_the_status_moved_on()
    {
        await MigrateAsync();
        using var db = CreateContext();

        var job = NewJob(status: JobStatus.Done);
        db.Jobs.Add(job);
        await db.SaveChangesAsync();

        var claimed = await new JobRepository(db).ForceClaimAsync(job.Id, JobStatus.Running, CancellationToken.None);

        claimed.Should().BeNull();
    }

    [Fact]
    public async Task Pending_jobs_can_be_counted_and_deleted_by_type()
    {
        await MigrateAsync();
        using var db = CreateContext();

        var projectId = Guid.NewGuid();
        Job ForProject(JobType type, JobStatus status = JobStatus.Pending)
        {
            var job = NewJob(status: status);
            job.Type = type;
            job.ProjectId = projectId;
            return job;
        }

        db.Jobs.AddRange(
            ForProject(JobType.EmbedChunk),
            ForProject(JobType.EmbedChunk),
            ForProject(JobType.EmbedChunk, JobStatus.Failed),
            ForProject(JobType.IndexFile));
        await db.SaveChangesAsync();

        var repository = new JobRepository(db);

        var counts = await repository.CountByTypeAsync(JobStatus.Pending, projectId, CancellationToken.None);
        counts.Should().BeEquivalentTo([new JobTypeCount(JobType.EmbedChunk, 2), new JobTypeCount(JobType.IndexFile, 1)]);

        var removed = await repository.DeleteByStatusAsync(JobStatus.Pending, projectId, JobType.EmbedChunk, null, CancellationToken.None);

        removed.Should().Be(2);
        var remaining = await db.Jobs.AsNoTracking().Select(j => new { j.Type, j.Status }).ToListAsync();
        remaining.Should().BeEquivalentTo([
            new { Type = JobType.EmbedChunk, Status = JobStatus.Failed },
            new { Type = JobType.IndexFile, Status = JobStatus.Pending },
        ]);
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

        var deleted = await new JobRepository(db).DeleteFinishedOlderThanAsync(cutoff, JobRetentionFilter.All, CancellationToken.None);

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

        var deleted = await new JobRepository(db).DeleteFinishedOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-7), JobRetentionFilter.All, CancellationToken.None);

        deleted.Should().Be(1, "falling back to CreatedAt stops such a row pinning itself in the table forever");
    }

    [Fact]
    public async Task DeleteFinishedOlderThanAsync_filter_limits_the_sweep_to_the_requested_type_and_status()
    {
        await MigrateAsync();
        using var db = CreateContext();

        var old = DateTimeOffset.UtcNow.AddDays(-3);
        Job Finished(JobType type, JobStatus status)
        {
            var job = NewJob(status: status, type: type);
            job.FinishedAt = old;
            return job;
        }

        var doneHook = Finished(JobType.ProcessHook, JobStatus.Done);
        var failedHook = Finished(JobType.ProcessHook, JobStatus.Failed);
        var doneEmbed = Finished(JobType.EmbedChunk, JobStatus.Done);
        db.Jobs.AddRange(doneHook, failedHook, doneEmbed);
        await db.SaveChangesAsync();

        var repository = new JobRepository(db);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-1);

        var onlyDoneHooks = await repository.DeleteFinishedOlderThanAsync(
            cutoff, new JobRetentionFilter(JobType.ProcessHook, OnlyStatus: JobStatus.Done), CancellationToken.None);
        onlyDoneHooks.Should().Be(1);

        var exceptHooks = await repository.DeleteFinishedOlderThanAsync(
            cutoff, new JobRetentionFilter(ExceptType: JobType.ProcessHook), CancellationToken.None);
        exceptHooks.Should().Be(1);

        var survivors = await db.Jobs.AsNoTracking().Select(j => j.Id).ToListAsync();
        survivors.Should().BeEquivalentTo([failedHook.Id], "a failed hook job is the only record of an unprocessed hook");
    }

    [Fact]
    public async Task DeleteByStatusAsync_never_removes_excluded_types()
    {
        await MigrateAsync();
        using var db = CreateContext();

        var projectId = Guid.NewGuid();
        Job ForProject(JobType type)
        {
            var job = NewJob(type: type);
            job.ProjectId = projectId;
            return job;
        }

        var hook = ForProject(JobType.ProcessHook);
        var remember = ForProject(JobType.RememberMemory);
        var embed = ForProject(JobType.EmbedChunk);
        db.Jobs.AddRange(hook, remember, embed);
        await db.SaveChangesAsync();

        var removed = await new JobRepository(db).DeleteByStatusAsync(
            JobStatus.Pending, projectId, null, [JobType.ProcessHook, JobType.RememberMemory], CancellationToken.None);

        removed.Should().Be(1);
        var survivors = await db.Jobs.AsNoTracking().Select(j => j.Id).ToListAsync();
        survivors.Should().BeEquivalentTo([hook.Id, remember.Id]);
    }

    [Fact]
    public async Task ListActiveByTypeAsync_returns_only_the_pending_and_running_jobs_of_that_type_and_project()
    {
        await MigrateAsync();
        using var db = CreateContext();

        var projectId = Guid.NewGuid();
        Job Job(JobType type, JobStatus status, Guid? project = null)
        {
            var job = NewJob(status: status, type: type);
            job.ProjectId = project ?? projectId;
            return job;
        }

        var pending = Job(JobType.ReindexProject, JobStatus.Pending);
        var running = Job(JobType.ReindexProject, JobStatus.Running);
        db.Jobs.AddRange(
            pending, running,
            Job(JobType.ReindexProject, JobStatus.Done),
            Job(JobType.ReindexProject, JobStatus.Failed),
            Job(JobType.IndexFile, JobStatus.Pending),
            Job(JobType.ReindexProject, JobStatus.Pending, Guid.NewGuid()));
        await db.SaveChangesAsync();

        var active = await new JobRepository(db).ListActiveByTypeAsync(JobType.ReindexProject, projectId, CancellationToken.None);

        active.Select(a => a.Id).Should().BeEquivalentTo(new[] { pending.Id, running.Id });
        active.Should().OnlyContain(a => a.CreatedAt != default);
    }

    [Fact]
    public async Task DequeueNextForProjectAsync_claims_only_the_job_types_the_lane_filter_allows()
    {
        await MigrateAsync();
        using var db = CreateContext();

        var projectId = Guid.NewGuid();
        Job Job(JobType type, int priority)
        {
            var job = NewJob(priority: priority, type: type);
            job.ProjectId = projectId;
            return job;
        }

        var hook = Job(JobType.ProcessHook, 1);
        var embed = Job(JobType.EmbedChunk, 9);
        var forget = Job(JobType.ForgetMemory, 5);
        db.Jobs.AddRange(hook, embed, forget);
        await db.SaveChangesAsync();
        var repository = new JobRepository(db);
        JobType[] light = [JobType.ProcessHook, JobType.ForgetMemory];

        var lightFirst = await repository.DequeueNextForProjectAsync(projectId, new JobLaneFilter(OnlyTypes: light), CancellationToken.None);
        var lightSecond = await repository.DequeueNextForProjectAsync(projectId, new JobLaneFilter(OnlyTypes: light), CancellationToken.None);
        var lightThird = await repository.DequeueNextForProjectAsync(projectId, new JobLaneFilter(OnlyTypes: light), CancellationToken.None);
        var ordinary = await repository.DequeueNextForProjectAsync(projectId, new JobLaneFilter(ExceptTypes: light), CancellationToken.None);

        lightFirst!.Id.Should().Be(forget.Id, "the higher priority light job goes first");
        lightSecond!.Id.Should().Be(hook.Id);
        lightThird.Should().BeNull("the EmbedChunk job is not light, even though it is still pending");
        ordinary!.Id.Should().Be(embed.Id);
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
