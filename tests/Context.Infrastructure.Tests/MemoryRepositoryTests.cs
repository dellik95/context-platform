using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Tests;

/// <summary>Runs against a real, on-disk SQLite file.</summary>
public sealed class MemoryRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-memoryrepo-tests-{Guid.NewGuid():N}.db");

    private ContextDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ContextDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        return new ContextDbContext(optionsBuilder.Options);
    }

    private static Memory NewMemory(Guid projectId, string title, string? topic) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Type = MemoryType.Discovery,
        Title = title,
        Body = "Body text.",
        Topic = topic,
        Confidence = 0.8,
        Tier = MemoryTier.Working,
        CreatedAt = DateTimeOffset.UtcNow,
        LastReinforcedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task ListByProjectAsync_round_trips_Configuration_and_Architecture_types()
    {
        using var db = CreateContext();
        await db.Database.MigrateAsync();

        var project = new Project { Id = Guid.NewGuid(), Name = "P1", RootPath = "/repo1", CreatedAt = DateTimeOffset.UtcNow };
        db.Projects.Add(project);
        var configMemory = new Memory
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Type = MemoryType.Configuration,
            Title = "Microsoft.OpenApi pinned to 2.7.5 to fix CVE-2026-49451",
            Body = "Directory.Packages.props transitively pins Microsoft.OpenApi to 2.7.5 via " +
                "CentralPackageTransitivePinningEnabled; Microsoft.AspNetCore.OpenApi 10.0.0 otherwise " +
                "pulls the vulnerable 2.0.0.",
            Topic = "NuGet dependency pinning",
            Confidence = 0.95,
            Tier = MemoryTier.Working,
            CreatedAt = DateTimeOffset.UtcNow,
            LastReinforcedAt = DateTimeOffset.UtcNow,
        };
        var infraMemory = new Memory
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Type = MemoryType.Architecture,
            Title = "Microsoft.Data.Sqlite central pin bumped 9.0.0 to 10.0.12",
            Body = "The stale 9.0.0 pin broke restore (NU1109) for every project pulling " +
                "Microsoft.SemanticKernel.Connectors.SqliteVec transitively, since that package requires >= 9.0.10.",
            Topic = "NuGet dependency pinning",
            Confidence = 0.95,
            Tier = MemoryTier.Working,
            CreatedAt = DateTimeOffset.UtcNow,
            LastReinforcedAt = DateTimeOffset.UtcNow,
        };
        db.Memories.AddRange(configMemory, infraMemory);
        await db.SaveChangesAsync();

        using var readDb = CreateContext();
        var repository = new MemoryRepository(readDb);

        var results = await repository.ListByProjectAsync(project.Id, includeSuperseded: false, CancellationToken.None);

        results.Should().HaveCount(2);
        results.Should().ContainSingle(m => m.Id == configMemory.Id && m.Type == MemoryType.Configuration);
        results.Should().ContainSingle(m => m.Id == infraMemory.Id && m.Type == MemoryType.Architecture);
    }

    private async Task<(ContextDbContext Db, Project Project)> SeedProjectAsync()
    {
        var db = CreateContext();
        await db.Database.MigrateAsync();
        var project = new Project { Id = Guid.NewGuid(), Name = "P1", RootPath = "/repo1", CreatedAt = DateTimeOffset.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();
        return (db, project);
    }

    [Fact]
    public async Task DeleteAsync_removes_evidence_entities_and_embedding_and_obsoletes_what_it_superseded()
    {
        var (db, project) = await SeedProjectAsync();
        await using var _ = db;
        var old = NewMemory(project.Id, "Old", null);
        var target = NewMemory(project.Id, "Target", null);
        var bystander = NewMemory(project.Id, "Bystander", null);
        db.Memories.AddRange(old, target, bystander);
        db.MemoryEmbeddings.Add(new MemoryEmbedding { MemoryId = target.Id, Vector = [1, 2, 3, 4], Dim = 1, ModelKey = "noop:noop" });
        await db.SaveChangesAsync();

        await db.Memories.Where(m => m.Id == old.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(m => m.Status, MemoryStatus.Superseded)
            .SetProperty(m => m.SupersededById, target.Id)
            .SetProperty(m => m.SupersededAt, DateTimeOffset.UtcNow));

        var repository = new MemoryRepository(db);
        await repository.AddEvidenceAsync(
        [
            new MemoryEvidence { ProjectId = project.Id, MemoryId = target.Id, Relation = EvidenceRelation.Created, Source = EvidenceSource.Explicit, ObservedAt = DateTimeOffset.UtcNow },
            new MemoryEvidence { ProjectId = project.Id, MemoryId = bystander.Id, Relation = EvidenceRelation.Created, Source = EvidenceSource.Explicit, ObservedAt = DateTimeOffset.UtcNow },
        ], CancellationToken.None);
        await repository.ReplaceEntitiesAsync(target.Id, [new MemoryEntity { ProjectId = project.Id, Key = "checkoutsession", Display = "CheckoutSession" }], CancellationToken.None);

        await repository.DeleteAsync(target.Id, CancellationToken.None);

        (await repository.GetByIdAsync(target.Id, CancellationToken.None)).Should().BeNull();
        (await repository.ListEvidenceAsync([target.Id, bystander.Id], CancellationToken.None)).Should().ContainSingle(e => e.MemoryId == bystander.Id);
        (await repository.ListEntitiesAsync([target.Id], CancellationToken.None)).Should().BeEmpty();
        (await db.MemoryEmbeddings.AsNoTracking().AnyAsync(e => e.MemoryId == target.Id)).Should().BeFalse();

        var orphan = await repository.GetByIdAsync(old.Id, CancellationToken.None);
        orphan!.Status.Should().Be(MemoryStatus.Obsolete);
        orphan.SupersededById.Should().BeNull();
        orphan.SupersededAt.Should().BeNull();
    }

    [Fact]
    public async Task ReplaceEntitiesAsync_replaces_the_set_and_live_key_lookup_skips_non_live_memories()
    {
        var (db, project) = await SeedProjectAsync();
        await using var _ = db;
        var live = NewMemory(project.Id, "Live", null);
        var obsolete = NewMemory(project.Id, "Obsolete", null);
        db.Memories.AddRange(live, obsolete);
        await db.SaveChangesAsync();

        var repository = new MemoryRepository(db);
        await repository.ReplaceEntitiesAsync(live.Id,
        [
            new MemoryEntity { ProjectId = project.Id, Key = "stale", Display = "Stale" },
        ], CancellationToken.None);
        await repository.ReplaceEntitiesAsync(live.Id,
        [
            new MemoryEntity { ProjectId = project.Id, Key = "checkoutsession", Display = "CheckoutSession" },
            new MemoryEntity { ProjectId = project.Id, Key = "checkoutsession", Display = "checkoutSession" },
            new MemoryEntity { ProjectId = project.Id, Key = "pay-15170", Display = "PAY-15170" },
        ], CancellationToken.None);
        await repository.ReplaceEntitiesAsync(obsolete.Id,
        [
            new MemoryEntity { ProjectId = project.Id, Key = "checkoutsession", Display = "CheckoutSession" },
        ], CancellationToken.None);
        await db.Memories.Where(m => m.Id == obsolete.Id).ExecuteUpdateAsync(setters => setters.SetProperty(m => m.Status, MemoryStatus.Obsolete));

        (await repository.ListEntitiesAsync([live.Id], CancellationToken.None)).Select(e => e.Key)
            .Should().BeEquivalentTo(["checkoutsession", "pay-15170"]);
        (await repository.ListLiveEntitiesByKeysAsync(project.Id, ["checkoutsession", "missing"], CancellationToken.None))
            .Should().ContainSingle(e => e.MemoryId == live.Id && e.Key == "checkoutsession");
    }

    [Fact]
    public async Task ListLiveByTopicKeyAsync_returns_only_live_memories_with_that_exact_key()
    {
        var (db, project) = await SeedProjectAsync();
        await using var _ = db;
        var match = NewMemory(project.Id, "Match", null);
        match.TopicKey = "checkout-session";
        var contested = NewMemory(project.Id, "Contested", null);
        contested.TopicKey = "checkout-session";
        contested.Status = MemoryStatus.Contested;
        var obsolete = NewMemory(project.Id, "Obsolete", null);
        obsolete.TopicKey = "checkout-session";
        obsolete.Status = MemoryStatus.Obsolete;
        var other = NewMemory(project.Id, "Other", null);
        other.TopicKey = "checkout-session-v2";
        db.Memories.AddRange(match, contested, obsolete, other);
        await db.SaveChangesAsync();

        var results = await new MemoryRepository(db).ListLiveByTopicKeyAsync(project.Id, "checkout-session", CancellationToken.None);

        results.Select(m => m.Id).Should().BeEquivalentTo([match.Id, contested.Id]);
    }

    [Fact]
    public async Task ListEvidenceAsync_returns_evidence_oldest_first()
    {
        var (db, project) = await SeedProjectAsync();
        await using var _ = db;
        var memory = NewMemory(project.Id, "M", null);
        db.Memories.Add(memory);
        await db.SaveChangesAsync();
        var t0 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        var repository = new MemoryRepository(db);
        await repository.AddEvidenceAsync(
        [
            new MemoryEvidence { ProjectId = project.Id, MemoryId = memory.Id, Relation = EvidenceRelation.Reinforced, Source = EvidenceSource.EventBatch, ObservedAt = t0.AddDays(2) },
            new MemoryEvidence { ProjectId = project.Id, MemoryId = memory.Id, Relation = EvidenceRelation.Created, Source = EvidenceSource.Explicit, ObservedAt = t0 },
        ], CancellationToken.None);

        var evidence = await repository.ListEvidenceAsync([memory.Id], CancellationToken.None);

        evidence.Select(e => e.Relation).Should().Equal(EvidenceRelation.Created, EvidenceRelation.Reinforced);
        evidence.Should().OnlyContain(e => e.Id != Guid.Empty);
    }

    [Fact]
    public async Task Reports_are_listed_newest_first_and_change_status_only_from_the_expected_one()
    {
        var (db, project) = await SeedProjectAsync();
        await using var _ = db;
        var t0 = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

        var repository = new MemoryRepository(db);
        var older = await repository.AddReportAsync(new MemoryConsolidationReport { ProjectId = project.Id, CreatedAt = t0, ReportJson = "{}" }, CancellationToken.None);
        var newer = await repository.AddReportAsync(new MemoryConsolidationReport { ProjectId = project.Id, CreatedAt = t0.AddHours(1), ReportJson = "{\"groups\":[]}" }, CancellationToken.None);

        (await repository.ListReportsAsync(project.Id, CancellationToken.None)).Select(r => r.Id).Should().Equal(newer.Id, older.Id);

        var appliedAt = t0.AddHours(2);
        (await repository.SetReportStatusAsync(newer.Id, ConsolidationReportStatus.Pending, ConsolidationReportStatus.Applied, appliedAt, null, CancellationToken.None)).Should().BeTrue();
        var stored = await repository.GetReportAsync(newer.Id, CancellationToken.None);
        stored!.Status.Should().Be(ConsolidationReportStatus.Applied);
        stored.AppliedAt.Should().Be(appliedAt);
        stored.ReportJson.Should().Be("{\"groups\":[]}");

        (await repository.SetReportStatusAsync(newer.Id, ConsolidationReportStatus.Pending, ConsolidationReportStatus.Discarded, null, null, CancellationToken.None)).Should().BeFalse();
        (await repository.GetReportAsync(newer.Id, CancellationToken.None))!.Status.Should().Be(ConsolidationReportStatus.Applied);

        (await repository.SetReportStatusAsync(older.Id, ConsolidationReportStatus.Pending, ConsolidationReportStatus.Applying, null, null, CancellationToken.None)).Should().BeTrue();
        (await repository.SetReportStatusAsync(older.Id, ConsolidationReportStatus.Pending, ConsolidationReportStatus.Applying, null, null, CancellationToken.None))
            .Should().BeFalse("a report is claimed for applying only once");
        (await repository.SetReportStatusAsync(older.Id, ConsolidationReportStatus.Applying, ConsolidationReportStatus.Applied, appliedAt, "{\"applied\":true}", CancellationToken.None)).Should().BeTrue();
        (await repository.GetReportAsync(older.Id, CancellationToken.None))!.ReportJson.Should().Be("{\"applied\":true}");

        (await repository.SetReportStatusAsync(Guid.NewGuid(), ConsolidationReportStatus.Pending, ConsolidationReportStatus.Discarded, null, null, CancellationToken.None)).Should().BeFalse();
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
