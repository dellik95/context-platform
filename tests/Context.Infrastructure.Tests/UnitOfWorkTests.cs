using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Tests;

public sealed class UnitOfWorkTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-uow-tests-{Guid.NewGuid():N}.db");

    private ContextDbContext CreateContext()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ContextDbContext>();
        optionsBuilder.UseSqlite($"Data Source={_dbPath}");
        return new ContextDbContext(optionsBuilder.Options);
    }

    private static Job NewJob() => new()
    {
        Id = Guid.NewGuid(),
        Type = JobType.ProcessHook,
        PayloadJson = "{}",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private async Task MigrateAsync()
    {
        using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    private async Task<int> JobCountAsync()
    {
        using var db = CreateContext();
        return await db.Jobs.CountAsync();
    }

    [Fact]
    public async Task Work_is_committed_when_it_completes()
    {
        await MigrateAsync();
        using var db = CreateContext();

        await ((IUnitOfWork)new UnitOfWork(db)).InTransactionAsync(async () =>
        {
            db.Jobs.Add(NewJob());
            await db.SaveChangesAsync();
        }, CancellationToken.None);

        (await JobCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Work_is_rolled_back_when_it_throws()
    {
        await MigrateAsync();
        using var db = CreateContext();

        IUnitOfWork unitOfWork = new UnitOfWork(db);
        var act = () => unitOfWork.InTransactionAsync(async () =>
        {
            db.Jobs.Add(NewJob());
            await db.SaveChangesAsync();
            throw new InvalidOperationException("boom");
        }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await JobCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_nested_call_joins_the_open_transaction_so_the_outer_failure_rolls_both_back()
    {
        await MigrateAsync();
        using var db = CreateContext();
        IUnitOfWork unitOfWork = new UnitOfWork(db);

        var act = () => unitOfWork.InTransactionAsync(async () =>
        {
            await unitOfWork.InTransactionAsync(async () =>
            {
                db.Jobs.Add(NewJob());
                await db.SaveChangesAsync();
            }, CancellationToken.None);

            (await JobCountAsync()).Should().Be(0, "the inner call must not commit on its own");
            throw new InvalidOperationException("outer failed");
        }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await JobCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task InTransaction_is_true_only_while_the_work_runs()
    {
        await MigrateAsync();
        using var db = CreateContext();
        IUnitOfWork unitOfWork = new UnitOfWork(db);
        var seen = new List<bool>();

        unitOfWork.InTransaction.Should().BeFalse();
        await unitOfWork.InTransactionAsync(async () =>
        {
            seen.Add(unitOfWork.InTransaction);
            await unitOfWork.InTransactionAsync(() =>
            {
                seen.Add(unitOfWork.InTransaction);
                return Task.CompletedTask;
            }, CancellationToken.None);
            seen.Add(unitOfWork.InTransaction);
        }, CancellationToken.None);

        seen.Should().Equal(true, true, true);
        unitOfWork.InTransaction.Should().BeFalse();
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
