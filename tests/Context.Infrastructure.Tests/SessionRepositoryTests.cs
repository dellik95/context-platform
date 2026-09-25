using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Tests;

/// <summary>
/// Repository-level test for <see cref="SessionRepository.GetByClaudeSessionIdAsync"/> -- runs
/// against a real, on-disk SQLite file (not an in-memory provider), matching
/// <see cref="SymbolRepositoryTests"/>'s pattern, since the method's whole point is to work around
/// a real SQLite/EF Core ORDER BY-over-DateTimeOffset limitation.
/// </summary>
public sealed class SessionRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-sessionrepo-tests-{Guid.NewGuid():N}.db");

    private ContextDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ContextDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        return new ContextDbContext(optionsBuilder.Options);
    }

    [Fact]
    public async Task GetByClaudeSessionIdAsync_returns_null_when_no_session_has_that_claudeSessionId()
    {
        using var db = CreateContext();
        await db.Database.MigrateAsync();

        var project = new Project { Id = Guid.NewGuid(), Name = "P1", RootPath = "/repo1", CreatedAt = DateTimeOffset.UtcNow };
        db.Projects.Add(project);
        db.Sessions.Add(NewSession(project.Id, "some-other-claude-session", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var repository = new SessionRepository(db);

        var result = await repository.GetByClaudeSessionIdAsync(project.Id, "no-such-session", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetByClaudeSessionIdAsync_returns_the_single_matching_session()
    {
        using var db = CreateContext();
        await db.Database.MigrateAsync();

        var project = new Project { Id = Guid.NewGuid(), Name = "P1", RootPath = "/repo1", CreatedAt = DateTimeOffset.UtcNow };
        var target = NewSession(project.Id, "claude-session-abc", DateTimeOffset.UtcNow);
        db.Projects.Add(project);
        db.Sessions.AddRange(target, NewSession(project.Id, "claude-session-xyz", DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        var repository = new SessionRepository(db);

        var result = await repository.GetByClaudeSessionIdAsync(project.Id, "claude-session-abc", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be(target.Id);
    }

    [Fact]
    public async Task GetByClaudeSessionIdAsync_returns_the_most_recently_started_session_when_duplicates_exist()
    {
        using var db = CreateContext();
        await db.Database.MigrateAsync();

        var project = new Project { Id = Guid.NewGuid(), Name = "P1", RootPath = "/repo1", CreatedAt = DateTimeOffset.UtcNow };
        var now = DateTimeOffset.UtcNow;
        var oldest = NewSession(project.Id, "claude-session-dup", now.AddMinutes(-10));
        var middle = NewSession(project.Id, "claude-session-dup", now.AddMinutes(-5));
        var newest = NewSession(project.Id, "claude-session-dup", now);

        db.Projects.Add(project);
        db.Sessions.AddRange(oldest, newest, middle);
        await db.SaveChangesAsync();

        var repository = new SessionRepository(db);

        var result = await repository.GetByClaudeSessionIdAsync(project.Id, "claude-session-dup", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Id.Should().Be(newest.Id);
    }

    [Fact]
    public async Task ClaimEventsForExtractionAsync_claims_nothing_below_threshold_then_everything_waiting_once_reached()
    {
        using var db = CreateContext();
        await db.Database.MigrateAsync();
        var (project, session) = await SeedSessionAsync(db);
        var repository = new SessionRepository(db);

        await AddEventsAsync(repository, session.Id, 2);
        (await repository.ClaimEventsForExtractionAsync(session.Id, minEvents: 3, CancellationToken.None)).Should().BeNull();

        await AddEventsAsync(repository, session.Id, 1);
        var batchId = await repository.ClaimEventsForExtractionAsync(session.Id, minEvents: 3, CancellationToken.None);

        batchId.Should().NotBeNull();
        (await repository.GetEventsByExtractionBatchAsync(project.Id, session.Id, batchId!.Value, CancellationToken.None))
            .Should().HaveCount(3);
        (await repository.ClaimEventsForExtractionAsync(session.Id, minEvents: 1, CancellationToken.None))
            .Should().BeNull("every event is already in a batch");
    }

    [Fact]
    public async Task DeleteEventsUpToAsync_keeps_events_whose_extraction_has_not_finished()
    {
        using var db = CreateContext();
        await db.Database.MigrateAsync();
        var (project, session) = await SeedSessionAsync(db);
        var repository = new SessionRepository(db);

        await AddEventsAsync(repository, session.Id, 2);
        var extracted = await repository.ClaimEventsForExtractionAsync(session.Id, 1, CancellationToken.None);
        await repository.MarkExtractionBatchDoneAsync(extracted!.Value, DateTimeOffset.UtcNow, CancellationToken.None);
        await AddEventsAsync(repository, session.Id, 1);
        await repository.ClaimEventsForExtractionAsync(session.Id, 1, CancellationToken.None);

        await repository.DeleteEventsUpToAsync(session.Id, DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None);

        var remaining = await repository.GetEventsAsync(project.Id, session.Id, CancellationToken.None);
        remaining.Should().ContainSingle().Which.ExtractedAt.Should().BeNull();
    }

    private static async Task<(Project Project, Session Session)> SeedSessionAsync(ContextDbContext db)
    {
        var project = new Project { Id = Guid.NewGuid(), Name = "P1", RootPath = "/repo1", CreatedAt = DateTimeOffset.UtcNow };
        var session = NewSession(project.Id, "claude-session", DateTimeOffset.UtcNow);
        db.Projects.Add(project);
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return (project, session);
    }

    private static async Task AddEventsAsync(SessionRepository repository, Guid sessionId, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await repository.AddEventAsync(
                new SessionEvent { SessionId = sessionId, Type = SessionEventType.ToolCall, PayloadJson = "{}", Timestamp = DateTimeOffset.UtcNow },
                CancellationToken.None);
        }
    }

    private static Session NewSession(Guid projectId, string claudeSessionId, DateTimeOffset startedAt) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        ClaudeSessionId = claudeSessionId,
        StartedAt = startedAt,
        EndedAt = null,
        Status = SessionStatus.Active
    };

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections by connection string, so the underlying OS file
        // handle can outlive the DbContext's own Dispose() -- clear the pool first or the file
        // deletes below intermittently throw "process cannot access the file".
        SqliteConnection.ClearAllPools();

        if (System.IO.File.Exists(_dbPath))
        {
            System.IO.File.Delete(_dbPath);
        }

        var wal = _dbPath + "-wal";
        var shm = _dbPath + "-shm";
        if (System.IO.File.Exists(wal))
        {
            System.IO.File.Delete(wal);
        }

        if (System.IO.File.Exists(shm))
        {
            System.IO.File.Delete(shm);
        }
    }
}
