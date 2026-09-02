using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using DomainFile = Context.Domain.Entities.File;

namespace Context.Infrastructure.Tests;

/// <summary>
/// Repository-level test for <see cref="SymbolRepository.CountByProjectAsync"/> (BACKLOG-06) --
/// runs against a real, on-disk SQLite file (not an in-memory provider) so the join/COUNT query is
/// exercised by SQLite itself, the same as production.
/// </summary>
public sealed class SymbolRepositoryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-symbolrepo-tests-{Guid.NewGuid():N}.db");

    private ContextDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ContextDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        return new ContextDbContext(optionsBuilder.Options);
    }

    [Fact]
    public async Task CountByProjectAsync_counts_only_symbols_belonging_to_non_deleted_files_of_the_given_project()
    {
        using var db = CreateContext();
        await db.Database.MigrateAsync();

        var project = new Project { Id = Guid.NewGuid(), Name = "P1", RootPath = "/repo1", CreatedAt = DateTimeOffset.UtcNow };
        var otherProject = new Project { Id = Guid.NewGuid(), Name = "P2", RootPath = "/repo2", CreatedAt = DateTimeOffset.UtcNow };

        var activeFile1 = NewFile(project.Id, "a.cs");
        var activeFile2 = NewFile(project.Id, "b.cs");
        var deletedFile = NewFile(project.Id, "deleted.cs", isDeleted: true);
        var otherProjectFile = NewFile(otherProject.Id, "c.cs");

        db.Projects.AddRange(project, otherProject);
        db.Files.AddRange(activeFile1, activeFile2, deletedFile, otherProjectFile);
        db.Symbols.AddRange(
            NewSymbol(activeFile1.Id, "Foo"),
            NewSymbol(activeFile1.Id, "Bar"),
            NewSymbol(activeFile2.Id, "Baz"),
            NewSymbol(deletedFile.Id, "ShouldBeExcludedBecauseFileIsDeleted"),
            NewSymbol(otherProjectFile.Id, "ShouldBeExcludedBecauseDifferentProject"));
        await db.SaveChangesAsync();

        var repository = new SymbolRepository(db);

        var count = await repository.CountByProjectAsync(project.Id, CancellationToken.None);

        count.Should().Be(3);
    }

    [Fact]
    public async Task CountByProjectAsync_returns_zero_for_a_project_with_no_files()
    {
        using var db = CreateContext();
        await db.Database.MigrateAsync();

        var project = new Project { Id = Guid.NewGuid(), Name = "Empty", RootPath = "/repo", CreatedAt = DateTimeOffset.UtcNow };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var repository = new SymbolRepository(db);

        var count = await repository.CountByProjectAsync(project.Id, CancellationToken.None);

        count.Should().Be(0);
    }

    private static DomainFile NewFile(Guid projectId, string relativePath, bool isDeleted = false) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        RelativePath = relativePath,
        ContentHash = Guid.NewGuid().ToString("N"),
        IsDeleted = isDeleted,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static Symbol NewSymbol(Guid fileId, string name) => new()
    {
        Id = Guid.NewGuid(),
        FileId = fileId,
        Kind = SymbolKind.Method,
        Name = name,
        QualifiedName = $"Ns.{name}",
        StartLine = 1,
        EndLine = 2,
        SignatureHash = Guid.NewGuid().ToString("N"),
        BodyHash = Guid.NewGuid().ToString("N")
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
