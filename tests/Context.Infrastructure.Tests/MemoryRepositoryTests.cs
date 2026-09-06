using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Tests;

/// <summary>
/// Repository-level test for <see cref="MemoryRepository.SearchAsync"/>'s <c>topic</c> filter --
/// runs against a real, on-disk SQLite file, matching <see cref="SessionRepositoryTests"/>'s pattern.
/// </summary>
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
    public async Task SearchAsync_with_topic_only_returns_memories_tagged_with_that_topic()
    {
        using var db = CreateContext();
        await db.Database.MigrateAsync();

        var project = new Project { Id = Guid.NewGuid(), Name = "P1", RootPath = "/repo1", CreatedAt = DateTimeOffset.UtcNow };
        db.Projects.Add(project);
        var authMemory = NewMemory(project.Id, "Auth memory", "Auth middleware");
        var otherMemory = NewMemory(project.Id, "Other memory", "Job dispatcher retry logic");
        db.Memories.AddRange(authMemory, otherMemory);
        await db.SaveChangesAsync();

        var repository = new MemoryRepository(db);

        var results = await repository.SearchAsync(project.Id, string.Empty, includeSuperseded: false, CancellationToken.None, topic: "Auth middleware");

        results.Should().ContainSingle(m => m.Id == authMemory.Id);
    }

    [Fact]
    public async Task SearchAsync_without_topic_returns_memories_regardless_of_topic()
    {
        using var db = CreateContext();
        await db.Database.MigrateAsync();

        var project = new Project { Id = Guid.NewGuid(), Name = "P1", RootPath = "/repo1", CreatedAt = DateTimeOffset.UtcNow };
        db.Projects.Add(project);
        db.Memories.AddRange(
            NewMemory(project.Id, "Auth memory", "Auth middleware"),
            NewMemory(project.Id, "Untagged memory", topic: null));
        await db.SaveChangesAsync();

        var repository = new MemoryRepository(db);

        var results = await repository.SearchAsync(project.Id, string.Empty, includeSuperseded: false, CancellationToken.None);

        results.Should().HaveCount(2);
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
