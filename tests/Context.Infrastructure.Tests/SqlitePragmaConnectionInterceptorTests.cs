using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Context.Infrastructure.Tests;

/// <summary>
/// BACKLOG-07: asserts <see cref="ContextDbContext.SqlitePragmaConnectionInterceptor"/> actually
/// sets <c>busy_timeout</c> (in addition to the pre-existing <c>journal_mode</c>/<c>foreign_keys</c>
/// pragmas) on a connection opened the normal way -- via <see cref="DbContextOptionsBuilder{TContext}.UseSqlite"/>
/// plus <c>AddInterceptors</c>, exactly as <see cref="ServiceCollectionExtensions.AddContextInfrastructure"/>
/// wires it up in production -- rather than asserting against the interceptor's source text.
/// </summary>
public sealed class SqlitePragmaConnectionInterceptorTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-pragma-tests-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task Opening_a_connection_through_the_normal_path_sets_busy_timeout_to_10_seconds()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ContextDbContext>();
        optionsBuilder
            .UseSqlite($"Data Source={_dbPath}")
            .AddInterceptors(new ContextDbContext.SqlitePragmaConnectionInterceptor());

        using var db = new ContextDbContext(optionsBuilder.Options);

        // Forces the connection open (running SqlitePragmaConnectionInterceptor.ConnectionOpenedAsync)
        // before we read the pragma back on the same connection.
        await db.Database.OpenConnectionAsync();

        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";
        var result = await command.ExecuteScalarAsync();

        Convert.ToInt64(result).Should().Be(10000);
    }

    [Fact]
    public async Task Opening_a_connection_through_the_normal_path_also_still_sets_wal_and_foreign_keys()
    {
        var optionsBuilder = new DbContextOptionsBuilder<ContextDbContext>();
        optionsBuilder
            .UseSqlite($"Data Source={_dbPath}")
            .AddInterceptors(new ContextDbContext.SqlitePragmaConnectionInterceptor());

        using var db = new ContextDbContext(optionsBuilder.Options);
        await db.Database.OpenConnectionAsync();

        var connection = db.Database.GetDbConnection();

        await using var journalModeCommand = connection.CreateCommand();
        journalModeCommand.CommandText = "PRAGMA journal_mode;";
        var journalMode = (string)(await journalModeCommand.ExecuteScalarAsync())!;
        journalMode.Should().BeEquivalentTo("wal");

        await using var foreignKeysCommand = connection.CreateCommand();
        foreignKeysCommand.CommandText = "PRAGMA foreign_keys;";
        var foreignKeys = Convert.ToInt64(await foreignKeysCommand.ExecuteScalarAsync());
        foreignKeys.Should().Be(1);
    }

    public void Dispose()
    {
        // Microsoft.Data.Sqlite pools connections by connection string, so the underlying OS file
        // handle can outlive the DbContext's own Dispose() -- clear the pool first or the file
        // deletes below intermittently throw "process cannot access the file".
        SqliteConnection.ClearAllPools();

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }

        var wal = _dbPath + "-wal";
        var shm = _dbPath + "-shm";
        if (File.Exists(wal))
        {
            File.Delete(wal);
        }

        if (File.Exists(shm))
        {
            File.Delete(shm);
        }
    }
}
