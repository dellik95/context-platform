using Context.Application.Abstractions;
using Context.Application.Adapters;
using Context.Application.Jobs.Handlers;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure;
using Context.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Application.Tests;

public sealed class DecayMemoryJobHandlerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-decay-tests-{Guid.NewGuid():N}.db");

    private DbContextOptions<ContextDbContext> Options =>
        new DbContextOptionsBuilder<ContextDbContext>().UseSqlite($"Data Source={_dbPath}").Options;

    [Fact]
    public async Task HandleAsync_retires_decayed_working_memories_without_deleting_anything()
    {
        var payments = Guid.NewGuid();
        var billing = Guid.NewGuid();
        var staleWorking = NewMemory(payments, "Stale working fact", ageDays: 120);
        var freshWorking = NewMemory(payments, "Fresh working fact", ageDays: 10);
        var staleLongTerm = NewMemory(payments, "Long-term fact", ageDays: 365, tier: MemoryTier.LongTerm);
        var stalePinned = NewMemory(payments, "Pinned fact", ageDays: 365, pinned: true);
        var staleContested = NewMemory(payments, "Contested fact", ageDays: 200, status: MemoryStatus.Contested);
        var freshPartner = NewMemory(payments, "Contesting fact", ageDays: 5, status: MemoryStatus.Contested);
        var staleSuperseded = NewMemory(payments, "Superseded fact", ageDays: 200, status: MemoryStatus.Superseded);
        var otherProjectStale = NewMemory(billing, "Billing fact", ageDays: 120);

        await using (var seed = new ContextDbContext(Options))
        {
            await seed.Database.MigrateAsync();
            seed.Projects.AddRange(
                new Project { Id = payments, Name = "Payments", RootPath = "/repo/payments", CreatedAt = DateTimeOffset.UtcNow },
                new Project { Id = billing, Name = "Billing", RootPath = "/repo/billing", CreatedAt = DateTimeOffset.UtcNow });
            seed.Memories.AddRange(
                staleWorking, freshWorking, staleLongTerm, stalePinned, staleContested, freshPartner, staleSuperseded, otherProjectStale);
            seed.MemoryEvidence.AddRange(
                Contradicted(staleContested, freshPartner),
                Contradicted(freshPartner, staleContested),
                Created(freshPartner));
            await seed.SaveChangesAsync();
        }

        await using (var db = new ContextDbContext(Options))
        {
            var repository = new MemoryRepository(db);
            var store = new MemoryStoreAdapter(repository, db, new Mock<IMemoryVectorIndex>().Object);
            var sut = new DecayMemoryJobHandler(new ProjectRepository(db), repository, store);
            await sut.HandleAsync(new Job { Type = JobType.DecayMemory, PayloadJson = "{}" }, CancellationToken.None);
        }

        await using var verify = new ContextDbContext(Options);
        var statuses = await verify.Memories.ToDictionaryAsync(m => m.Id, m => m.Status);
        statuses.Should().HaveCount(8);
        statuses[staleWorking.Id].Should().Be(MemoryStatus.Obsolete);
        statuses[staleContested.Id].Should().Be(MemoryStatus.Obsolete);
        statuses[otherProjectStale.Id].Should().Be(MemoryStatus.Obsolete);
        statuses[freshWorking.Id].Should().Be(MemoryStatus.Active);
        statuses[staleLongTerm.Id].Should().Be(MemoryStatus.Active);
        statuses[stalePinned.Id].Should().Be(MemoryStatus.Active);
        statuses[staleSuperseded.Id].Should().Be(MemoryStatus.Superseded);
        statuses[freshPartner.Id].Should().Be(MemoryStatus.Active, "its only contest partner was retired");
    }

    private static MemoryEvidence Contradicted(DomainMemory memory, DomainMemory partner) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = memory.ProjectId,
        MemoryId = memory.Id,
        Relation = EvidenceRelation.Contradicted,
        Source = EvidenceSource.EventBatch,
        SourceMemoryId = partner.Id,
        ObservedAt = memory.CreatedAt,
    };

    private static MemoryEvidence Created(DomainMemory memory) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = memory.ProjectId,
        MemoryId = memory.Id,
        Relation = EvidenceRelation.Created,
        Source = EvidenceSource.EventBatch,
        ObservedAt = memory.CreatedAt,
    };

    private static DomainMemory NewMemory(
        Guid projectId,
        string title,
        int ageDays,
        MemoryType type = MemoryType.Fact,
        MemoryTier tier = MemoryTier.Working,
        MemoryStatus status = MemoryStatus.Active,
        bool pinned = false,
        double confidence = 0.7) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Type = type,
        Tier = tier,
        Status = status,
        Pinned = pinned,
        Confidence = confidence,
        Title = title,
        Body = title + ".",
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-ageDays),
        LastReinforcedAt = DateTimeOffset.UtcNow.AddDays(-ageDays),
    };

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
