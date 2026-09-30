using System.Text.Json;
using Context.Domain.Entities;
using Context.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Context.Infrastructure.Tests;

/// <summary>
/// Seeds single-hash EmbedChunk jobs in the pre-batching payload shape, migrates to latest and checks
/// they become batch jobs. Payloads are read with a mirror of <c>EmbedChunkPayload</c>/<c>EmbedChunkItem</c>
/// (Context.Application is not referenced here).
/// </summary>
public sealed class BatchPendingEmbedChunkJobsMigrationTests : IDisposable
{
    private const string PreviousMigration = "20260928152330_MemoryKnowledgeModel";

    private static readonly Guid ProjectA = Guid.NewGuid();
    private static readonly Guid ProjectB = Guid.NewGuid();
    private static readonly DateTimeOffset Created = new(2026, 9, 28, 14, 0, 0, TimeSpan.Zero);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-migration-tests-{Guid.NewGuid():N}.db");

    private ContextDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ContextDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        return new ContextDbContext(optionsBuilder.Options);
    }

    private static string Id(Guid id) => id.ToString().ToUpperInvariant();

    private static string At(DateTimeOffset at) => at.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz");

    [Fact]
    public async Task Migration_merges_waiting_jobs_into_batches_per_project_and_keeps_finished_ones()
    {
        var sql = "";
        var chunkByHash = new Dictionary<string, List<Guid>>();
        for (var i = 0; i < 18; i++)
        {
            sql += OldJob(ProjectA, $"h{i:D2}", JobStatus.Pending, Created.AddSeconds(i), chunkByHash);
        }

        sql += OldJob(ProjectA, "h00", JobStatus.Pending, Created.AddMinutes(5), chunkByHash);
        sql += OldJob(ProjectA, "h18", JobStatus.Running, Created.AddMinutes(6), chunkByHash);
        sql += OldJob(ProjectB, "b0", JobStatus.Pending, Created, chunkByHash);
        var failedId = Guid.NewGuid();
        sql += OldJob(ProjectA, "failed", JobStatus.Failed, Created, chunkByHash, failedId, attempts: 5, lastError: "boom");
        sql += "INSERT INTO Jobs (Id, Type, PayloadJson, Status, Priority, Attempts, CreatedAt) VALUES " +
            $"('{Id(Guid.NewGuid())}', 7, 'p', 0, 10, 0, '{At(Created)}');";

        await using (var db = CreateContext())
        {
            await db.GetService<IMigrator>().MigrateAsync(PreviousMigration);
            await db.Database.ExecuteSqlRawAsync(sql);
            await db.Database.MigrateAsync();
        }

        await using var readDb = CreateContext();
        var jobs = await readDb.Jobs.AsNoTracking().Where(j => j.Type == JobType.EmbedChunk).ToListAsync();

        var pendingA = jobs.Where(j => j.ProjectId == ProjectA && j.Status == JobStatus.Pending).OrderBy(j => j.CreatedAt).ToList();
        pendingA.Select(j => Read(j).Items.Count).Should().Equal(16, 3);
        pendingA.Should().OnlyContain(j => j.Attempts == 0 && j.Priority == 0 && j.LastError == null && j.NextRetryAt == null);
        pendingA[0].CreatedAt.Should().Be(Created);

        pendingA.Should().OnlyContain(j => Read(j).ProjectId == ProjectA);
        Read(pendingA[0]).Items.Select(i => i.ContentHash).Should().BeEquivalentTo(Enumerable.Range(0, 16).Select(i => $"h{i:D2}"));
        Read(pendingA[1]).Items.Select(i => i.ContentHash).Should().BeEquivalentTo(new[] { "h16", "h17", "h18" });
        var itemsA = pendingA.SelectMany(j => Read(j).Items).ToList();
        itemsA.Should().OnlyContain(i => i.Text == $"text {i.ContentHash}" && i.ChunkIds.ToHashSet().SetEquals(chunkByHash[i.ContentHash]));
        itemsA.Single(i => i.ContentHash == "h00").ChunkIds.Should().HaveCount(2, "the duplicate job for h00 pools its chunk ids into the same item");

        var pendingB = jobs.Should().ContainSingle(j => j.ProjectId == ProjectB).Subject;
        pendingB.Status.Should().Be(JobStatus.Pending);
        Read(pendingB).Items.Should().ContainSingle(i => i.ContentHash == "b0");

        var failed = jobs.Should().ContainSingle(j => j.Status == JobStatus.Failed).Subject;
        failed.Id.Should().Be(failedId);
        failed.Attempts.Should().Be(5);
        failed.LastError.Should().Be("boom");
        Read(failed).Items.Should().ContainSingle().Which.ChunkIds.Should().Equal(chunkByHash["failed"]);

        jobs.Should().HaveCount(4);
        (await readDb.Jobs.AsNoTracking().CountAsync(j => j.Type == JobType.ExtractMemory)).Should().Be(1);
    }

    [Fact]
    public async Task Down_splits_batches_back_into_one_job_per_content_hash()
    {
        var chunkByHash = new Dictionary<string, List<Guid>>();
        var sql = OldJob(ProjectA, "h1", JobStatus.Pending, Created, chunkByHash) +
            OldJob(ProjectA, "h2", JobStatus.Pending, Created.AddSeconds(1), chunkByHash);

        await using (var db = CreateContext())
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            await db.Database.ExecuteSqlRawAsync(sql);
            await db.Database.MigrateAsync();
            await migrator.MigrateAsync(PreviousMigration);
        }

        await using var readDb = CreateContext();
        var payloads = (await readDb.Jobs.AsNoTracking().Where(j => j.Type == JobType.EmbedChunk).ToListAsync())
            .Select(j => JsonDocument.Parse(j.PayloadJson).RootElement)
            .ToList();

        payloads.Select(p => p.GetProperty("ContentHash").GetString()).Should().BeEquivalentTo(new[] { "h1", "h2" });
        payloads.Should().OnlyContain(p =>
            p.GetProperty("ProjectId").GetGuid() == ProjectA &&
            p.GetProperty("ChunkIds").EnumerateArray().Select(c => c.GetGuid()).SequenceEqual(chunkByHash[p.GetProperty("ContentHash").GetString()!]));
    }

    private static string OldJob(
        Guid projectId, string hash, JobStatus status, DateTimeOffset createdAt, Dictionary<string, List<Guid>> chunkByHash,
        Guid? id = null, int attempts = 0, string? lastError = null)
    {
        var chunkId = Guid.NewGuid();
        if (!chunkByHash.TryGetValue(hash, out var chunks))
        {
            chunkByHash[hash] = chunks = [];
        }

        chunks.Add(chunkId);
        // ExecuteSqlRawAsync formats the SQL, so the JSON's braces are doubled.
        var payload = JsonSerializer.Serialize(new { ProjectId = projectId, ChunkIds = new[] { chunkId }, ContentHash = hash, Text = $"text {hash}" })
            .Replace("{", "{{").Replace("}", "}}");
        var error = lastError is null ? "NULL" : $"'{lastError}'";
        return "INSERT INTO Jobs (Id, Type, PayloadJson, Status, Priority, Attempts, LastError, CreatedAt, ProjectId) VALUES " +
            $"('{Id(id ?? Guid.NewGuid())}', 5, '{payload}', {(int)status}, 0, {attempts}, {error}, '{At(createdAt)}', '{Id(projectId)}');";
    }

    private static Payload Read(Job job) => JsonSerializer.Deserialize<Payload>(job.PayloadJson)!;

    private sealed record Payload(Guid ProjectId, IReadOnlyList<Item> Items);

    private sealed record Item(string ContentHash, string Text, IReadOnlyList<Guid> ChunkIds);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }
}
