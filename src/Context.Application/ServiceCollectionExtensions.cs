using Context.Application.Abstractions;
using Context.Application.Adapters;
using Context.Application.Indexing;
using Context.Application.Jobs;
using Context.Application.Jobs.Handlers;
using Context.Graph.Abstractions;
using Context.Infrastructure;
using Context.Memory.Abstractions;
using Context.Retrieval;
using Context.Retrieval.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Context.Application;

/// <summary>
/// DI wiring for Context.Application, the integration layer. This is the one method
/// <c>Context.Host</c> calls, <b>after</b> it has already called every other layer's own
/// <c>AddContextX</c> (<c>AddContextInfrastructure</c>, <c>AddContextIndexing</c>,
/// <c>AddContextGraph</c>, <c>AddContextRetrieval</c>, <c>AddContextMemory</c>,
/// <c>AddContextProviders</c>) — those calls register everything <em>except</em> the small seam
/// interfaces each layer deliberately left for its caller to implement against the real
/// repositories/providers (see CONTRACTS.md §11 and each layer's own <c>ServiceCollectionExtensions</c>
/// remarks). <see cref="AddContextApplication"/> supplies exactly those seam implementations, plus
/// the pieces that only make sense once every layer is wired together: <see cref="JobDispatcher"/>,
/// <see cref="ContextEngine"/> and <see cref="IndexingPipeline"/>.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <param name="projectDataDir">
    /// <c>{repo-root}/.context/</c> (CONTRACTS.md §9) — the same directory whose <c>context.db</c>
    /// was passed to <c>AddContextInfrastructure</c>. Needed here for two things: computing the
    /// same SQLite connection string for the singleton-safe <see cref="IDbContextFactory{TContext}"/>
    /// registration below, and <c>BackupDbJobHandler</c>'s <c>VACUUM INTO ... /backups/</c> target.
    /// </param>
    public static IServiceCollection AddContextApplication(this IServiceCollection services, string projectDataDir)
    {
        services.AddSingleton(new ContextApplicationOptions { ProjectDataDir = projectDataDir });

        // A handful of Context.Application adapters are consumed by singletons registered in
        // Context.Graph / Context.Retrieval (the warm graph cache and the hybrid search engine
        // both need to outlive any one request). Those adapters cannot constructor-inject the
        // pooled, Scoped ContextDbContext that AddContextInfrastructure's AddDbContextPool
        // registers - DI rejects a Scoped dependency captured by a singleton - so they instead
        // open a short-lived context per call via IDbContextFactory<ContextDbContext>, registered
        // here against the same database file. See the remarks on GraphPersistenceAdapter and
        // SqliteSearchIndex.
        var dbPath = Path.Combine(projectDataDir, "context.db");
        services.AddPooledDbContextFactory<ContextDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));

        AddSeamAdapters(services);
        AddJobHandlers(services);
        AddJobDispatcher(services);
        AddIndexingPipeline(services);
        AddContextEngine(services);

        return services;
    }

    /// <summary>
    /// Implements every small cross-project seam interface the other layers defined so they could
    /// be composed without illegal project references (task brief §1).
    /// </summary>
    private static void AddSeamAdapters(IServiceCollection services)
    {
        // Consumed by Context.Graph's singleton GraphCacheProvider/GraphStore -> must itself be a
        // singleton (see IDbContextFactory remark above).
        services.AddSingleton<IGraphPersistence, GraphPersistenceAdapter>();

        // Consumed by Context.Retrieval's singleton HybridSearchEngine -> must itself be a
        // singleton (see IDbContextFactory remark above).
        services.AddSingleton<ISearchIndex, SqliteSearchIndex>();

        // Also consumed by the singleton HybridSearchEngine, but their own dependencies
        // (Context.Providers' IEmbeddingProvider/IRerankerProvider) are themselves registered
        // singleton by AddContextProviders in every built-in configuration, so these are safe as
        // singletons too (and must be, for the same DI-lifetime reason as the two adapters above).
        services.AddSingleton<IEmbeddingLookup, EmbeddingLookupAdapter>();
        services.AddSingleton<IReranker, RerankerAdapter>();

        // Consumed by Context.Memory's Scoped ISessionSummarizer/IMemoryExtractor
        // (LlmSessionSummarizer / LlmMemoryExtractor, registered Scoped by AddContextMemory) ->
        // Scoped is correct and sufficient here; both talk to Scoped repositories/DbContext.
        services.AddScoped<IMemoryStore, MemoryStoreAdapter>();
        services.AddScoped<ISessionEventSource, SessionEventSourceAdapter>();

        // ContextPackBuilder is registered by AddContextRetrieval as the singleton
        // IContextPackBuilder implementation. ContextEngine needs the concrete type directly (see
        // its own remarks) to reach the richer BuildAsync overload that accepts historical
        // notes/open issues - this registration resolves both to the exact same instance rather
        // than constructing (or requiring callers to register) a second one.
        services.AddSingleton(sp => (ContextPackBuilder)sp.GetRequiredService<IContextPackBuilder>());
    }

    /// <summary>
    /// Registers every <see cref="IJobHandler"/> as Scoped: <see cref="JobDispatcher"/> resolves
    /// them from a fresh <see cref="IServiceScope"/> per job specifically so their Scoped
    /// repository dependencies are safe (see <see cref="IJobHandler"/>'s own remarks).
    /// </summary>
    private static void AddJobHandlers(IServiceCollection services)
    {
        services.AddScoped<IJobHandler, IndexFileJobHandler>();
        services.AddScoped<IJobHandler, DeleteFileJobHandler>();
        services.AddScoped<IJobHandler, ParseFileJobHandler>();
        services.AddScoped<IJobHandler, ResolveRelationsJobHandler>();
        services.AddScoped<IJobHandler, UpdateGraphJobHandler>();
        services.AddScoped<IJobHandler, EmbedChunkJobHandler>();
        services.AddScoped<IJobHandler, SummarizeSessionJobHandler>();
        services.AddScoped<IJobHandler, ExtractMemoryJobHandler>();
        services.AddScoped<IJobHandler, DecayMemoryJobHandler>();
        services.AddScoped<IJobHandler, BackupDbJobHandler>();
    }

    /// <summary>
    /// Registers <see cref="JobDispatcher"/> exactly once as a singleton, and exposes that same
    /// instance under both roles it plays: the <see cref="IJobQueue"/> every scoped consumer
    /// enqueues work through, and the <see cref="IHostedService"/> <c>Context.Host</c> starts/stops
    /// with the application's own lifetime (which runs <see cref="JobDispatcher.HydrateOnStartupAsync"/>
    /// then <see cref="JobDispatcher.StartProcessingAsync"/> - see its own remarks).
    /// </summary>
    private static void AddJobDispatcher(IServiceCollection services)
    {
        services.AddSingleton<JobDispatcher>();
        services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<JobDispatcher>());
        services.AddHostedService(sp => sp.GetRequiredService<JobDispatcher>());
    }

    private static void AddIndexingPipeline(IServiceCollection services)
    {
        services.AddScoped<IIndexingPipeline, IndexingPipeline>();
    }

    private static void AddContextEngine(IServiceCollection services)
    {
        services.AddSingleton<SessionDeliveryCache>();
        services.AddScoped<IContextEngine, ContextEngine>();
    }
}
