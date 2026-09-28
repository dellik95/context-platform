using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Context.Infrastructure;

/// <summary>
/// Design-time factory used by <c>dotnet ef migrations</c> / <c>dotnet ef database update</c>.
/// Resolves the SQLite database path from the <c>CONTEXT_PLATFORM_DB</c> environment variable
/// if set, otherwise defaults to <c>.context/context.db</c> relative to the working directory.
/// </summary>
public class ContextDbContextFactory : IDesignTimeDbContextFactory<ContextDbContext>
{
    public const string DefaultDbPath = ".context/context.db";
    public const string DbPathEnvironmentVariable = "CONTEXT_PLATFORM_DB";

    public ContextDbContext CreateDbContext(string[] args)
    {
        var dbPath = Environment.GetEnvironmentVariable(DbPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            dbPath = DefaultDbPath;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var optionsBuilder = new DbContextOptionsBuilder<ContextDbContext>();
        optionsBuilder.UseSqlite($"Data Source={dbPath}");

        return new ContextDbContext(optionsBuilder.Options);
    }
}
