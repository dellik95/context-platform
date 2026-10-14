using Context.Infrastructure.Abstractions;
using Context.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Context.Infrastructure;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the pooled <see cref="ContextDbContext"/> (SQLite, WAL mode, FK enforcement —
    /// see <see cref="ContextDbContext"/>) and every repository implementation under
    /// <c>Context.Infrastructure.Repositories</c>.
    /// </summary>
    /// <param name="dbPath">
    /// Path to the SQLite database file, e.g. <c>{repo-root}/.context/context.db</c>
    /// (CONTRACTS.md §9). Relative paths are resolved against the current working directory
    /// by the SQLite provider.
    /// </param>
    public static IServiceCollection AddContextInfrastructure(this IServiceCollection services, string dbPath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        services.AddDbContextPool<ContextDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}")
                   .AddInterceptors(new ContextDbContext.SqlitePragmaConnectionInterceptor()));

        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IProjectRepository, ProjectRepository>();
        services.AddScoped<IFileRepository, FileRepository>();
        services.AddScoped<ISymbolRepository, SymbolRepository>();
        services.AddScoped<IChunkRepository, ChunkRepository>();
        services.AddScoped<IEmbeddingRepository, EmbeddingRepository>();
        services.AddScoped<IGraphRepository, GraphRepository>();
        services.AddScoped<ISessionRepository, SessionRepository>();
        services.AddScoped<IMemoryRepository, MemoryRepository>();
        services.AddScoped<IJobRepository, JobRepository>();
        services.AddScoped<IHookInvocationRepository, HookInvocationRepository>();
        services.AddScoped<IUsageRecorder, UsageRecorder>();

        return services;
    }
}
