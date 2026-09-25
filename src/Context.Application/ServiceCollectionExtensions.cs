using Context.Application.Abstractions;
using Context.Application.Adapters;
using Context.Application.Indexing;
using Context.Application.Jobs;
using Context.Application.Jobs.Handlers;
using Context.Application.Providers;
using Context.Application.Scheduling;
using Context.Application.Settings;
using Context.Graph.Abstractions;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Context.Infrastructure.Repositories;
using Context.Memory.Abstractions;
using Context.Providers.Abstractions;
using Context.Retrieval;
using Context.Retrieval.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Context.Application;

/// <summary>
/// DI wiring for Context.Application, the integration layer. This is the one method
/// <c>Context.Dashboard.Server</c> calls, <b>after</b> it has already called every other layer's own
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
    /// <param name="projectRoot">
    /// <c>{repo-root}</c> — needed by provider-options setup classes (<see cref="Providers.OllamaOptionsSetup"/>)
    /// that read the Dashboard Settings tab's saved <c>.claude/context-platform.local.json</c> override.
    /// </param>
    /// <param name="projectDataDir">
    /// <c>{repo-root}/.context/</c> (CONTRACTS.md §9) — the same directory whose <c>context.db</c>
    /// was passed to <c>AddContextInfrastructure</c>. Needed here for two things: computing the
    /// same SQLite connection string for the singleton-safe <see cref="IDbContextFactory{TContext}"/>
    /// registration below, and <c>BackupDbJobHandler</c>'s <c>VACUUM INTO ... /backups/</c> target.
    /// </param>
    public static IServiceCollection AddContextApplication(this IServiceCollection services, string projectRoot, string projectDataDir)
    {
        services.AddSingleton(new ContextApplicationOptions { ProjectRoot = projectRoot, ProjectDataDir = projectDataDir });
        services.ConfigureOptions<Providers.OllamaOptionsSetup>();

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

        // Singleton: its own consumers (HybridSearchEngine, SqliteSearchIndex, IFileSummarizer,
        // every Scoped job handler) span both Singleton and Scoped lifetimes, so this can't itself
        // be Scoped — see ProjectProviderResolver's own remarks for how it safely reaches the
        // Scoped IProjectSettingsService from here anyway.
        services.AddSingleton<IProviderResolver, ProjectProviderResolver>();

        AddSeamAdapters(services, dbPath);
        AddJobHandlers(services);
        AddJobDispatcher(services);
        AddIndexingPipeline(services);
        AddContextEngine(services);

        // Real default: LlmFileSummarizer already degrades to a no-op internally when the
        // configured IChatClient is "noop", so (unlike IIndexingProgressReporter above) there's
        // no separate Null-then-override registration needed here.
        services.AddSingleton<IFileSummarizer, LlmFileSummarizer>();

        // Default/fallback so Context.Application has zero hard dependency on SignalR;
        // Context.Dashboard.Server overrides this with a SignalR-backed reporter (registration order —
        // last AddSingleton for a given interface wins).
        services.AddSingleton<IIndexingProgressReporter>(NullIndexingProgressReporter.Instance);
        services.AddSingleton<Context.Application.Notifications.IProjectActivityNotifier>(
            Context.Application.Notifications.NullProjectActivityNotifier.Instance);
        services.AddSingleton<Context.Application.Notifications.IJobCountsNotifier>(
            Context.Application.Notifications.NullJobCountsNotifier.Instance);
        services.AddSingleton<Context.Application.Notifications.IHookInvocationNotifier>(
            Context.Application.Notifications.NullHookInvocationNotifier.Instance);

        // Default/fallback so Context.Application has zero hard dependency on Redis;
        // Context.Dashboard.Server overrides this with a Redis-backed IJobSignal when
        // multi-instance operation is configured (registration order — last AddSingleton for a
        // given interface wins), so more than one Dashboard.Server process can share one job queue.
        services.AddSingleton<IJobSignal, InMemoryJobSignal>();
        AddProjectSettings(services);

        return services;
    }

    /// <summary>
    /// Implements every small cross-project seam interface the other layers defined so they could
    /// be composed without illegal project references (task brief §1).
    /// </summary>
    private static void AddSeamAdapters(IServiceCollection services, string dbPath)
    {
        // Consumed by Context.Graph's singleton GraphCacheProvider/GraphStore -> must itself be a
        // singleton (see IDbContextFactory remark above).
        services.AddSingleton<IGraphPersistence, GraphPersistenceAdapter>();

        // Consumed by SqliteSearchIndex below (and by EmbedChunkJobHandler, Scoped, on the write
        // side) -> singleton for the same IDbContextFactory-captured-by-a-singleton reason as
        // every other adapter here; it also caches one SqliteCollection per embedding provider for
        // the process's lifetime (see its own remarks).
        services.AddSingleton<IChunkVectorIndex>(sp =>
            new SqliteChunkVectorIndex(dbPath, sp.GetRequiredService<IDbContextFactory<ContextDbContext>>()));

        // Same seam/lifetime reasoning as IChunkVectorIndex above, applied to Memory so
        // memory_search can rank by semantic similarity instead of only substring matching
        // (consumed by the Scoped MemoryStoreAdapter on the write side and by the Scoped
        // ContextEngine on the read side, both fine to depend on a singleton).
        services.AddSingleton<IMemoryVectorIndex>(sp =>
            new SqliteMemoryVectorIndex(dbPath, sp.GetRequiredService<IDbContextFactory<ContextDbContext>>()));

        // Same seam/lifetime reasoning as IMemoryVectorIndex above, applied to SessionSummaryChunk
        // so session_summary_search can rank by semantic similarity instead of only substring
        // matching (consumed by the Scoped SummarizeSessionJobHandler on the write side and by the
        // Scoped ContextEngine on the read side, both fine to depend on a singleton).
        services.AddSingleton<ISessionSummaryVectorIndex>(_ => new SqliteSessionSummaryVectorIndex(dbPath));

        // Chunk/Memory rows are deleted through the repositories in Context.Infrastructure, which
        // cannot see the vector indexes above (they live here). These decorations re-register the
        // interfaces over Context.Infrastructure's own registrations -- last registration wins for
        // a single resolve -- so every delete prunes the vector store too, wherever it is issued from.
        services.AddScoped<ChunkRepository>();
        services.AddScoped<IChunkRepository>(sp => new VectorPruningChunkRepository(
            sp.GetRequiredService<ChunkRepository>(), sp.GetRequiredService<IChunkVectorIndex>()));

        services.AddScoped<MemoryRepository>();
        services.AddScoped<IMemoryRepository>(sp => new VectorPruningMemoryRepository(
            sp.GetRequiredService<MemoryRepository>(), sp.GetRequiredService<IMemoryVectorIndex>()));

        // Consumed by Context.Retrieval's singleton HybridSearchEngine -> must itself be a
        // singleton (see IDbContextFactory remark above).
        services.AddSingleton<ISearchIndex, SqliteSearchIndex>();

        // Also consumed by the singleton HybridSearchEngine, so must itself be singleton; safe to
        // be, since it resolves per-project providers through the singleton-safe IProviderResolver
        // above rather than constructor-injecting IEmbeddingGenerator directly.
        services.AddSingleton<IEmbeddingLookup, EmbeddingLookupAdapter>();
        services.AddSingleton<IReranker, RerankerAdapter>();

        // Consumed by Context.Memory's Scoped ISessionSummarizer/IMemoryExtractor
        // (LlmSessionSummarizer / LlmMemoryExtractor, registered Scoped by AddContextMemory) ->
        // Scoped is correct and sufficient here; both talk to Scoped repositories/DbContext.
        services.AddScoped<IMemoryStore, MemoryStoreAdapter>();
        services.AddScoped<ISessionEventSource, SessionEventSourceAdapter>();

        // Same seam pattern as the two above, forwarding to Context.Infrastructure's own
        // IUsageRecorder (registered by AddContextInfrastructure) so LlmSessionSummarizer /
        // LlmMemoryExtractor can record token usage (BACKLOG-27) without depending on
        // Context.Infrastructure/EF Core directly.
        services.AddScoped<Context.Memory.Abstractions.IUsageRecorder, UsageRecorderAdapter>();

        // Same seam pattern again: MemoryConsolidator reads its per-project thresholds through
        // this instead of depending on IProjectSettingsService directly (Context.Memory can't
        // reference Context.Application - see the adapter's remarks).
        services.AddScoped<Context.Memory.Abstractions.IMemoryConsolidationSettingsProvider, MemoryConsolidationSettingsProviderAdapter>();

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
        services.AddScoped<IJobHandler, ResolveRelationsJobHandler>();
        services.AddScoped<IJobHandler, UpdateGraphJobHandler>();
        services.AddScoped<IJobHandler, EmbedChunkJobHandler>();
        services.AddScoped<IJobHandler, SummarizeSessionJobHandler>();
        services.AddScoped<IJobHandler, ExtractMemoryJobHandler>();
        services.AddScoped<IJobHandler, DecayMemoryJobHandler>();
        services.AddScoped<IJobHandler, BackupDbJobHandler>();
        services.AddScoped<IJobHandler, RecreateEmbeddingsJobHandler>();
        services.AddScoped<IJobHandler, CompactMemoryJobHandler>();
    }

    /// <summary>
    /// Registers <see cref="JobDispatcher"/> exactly once as a singleton, and exposes that same
    /// instance under both roles it plays: the <see cref="IJobQueue"/> every scoped consumer
    /// enqueues work through, and the <see cref="IHostedService"/> <c>Context.Dashboard.Server</c> starts/stops
    /// with the application's own lifetime (which runs <see cref="JobDispatcher.HydrateOnStartupAsync"/>
    /// then <see cref="JobDispatcher.StartProcessingAsync"/> - see its own remarks).
    /// </summary>
    private static void AddJobDispatcher(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<JobDispatcher>();
        services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<JobDispatcher>());
        services.AddHostedService(sp => sp.GetRequiredService<JobDispatcher>());
        services.AddHostedService<ReindexSchedulerService>();
        services.AddHostedService<SessionSummaryScheduler>();
        services.AddHostedService<StaleSessionScheduler>();
        services.AddHostedService<HookInvocationRetentionScheduler>();
        services.AddHostedService<JobRetentionScheduler>();
        services.AddHostedService<MemoryCompactionScheduler>();
        services.AddHostedService<MaintenanceScheduler>();
    }

    private static void AddIndexingPipeline(IServiceCollection services)
    {
        services.AddScoped<IIndexingPipeline, IndexingPipeline>();

        // Singleton: shared between the request that starts a reindex (ProjectEndpoints.TriggerIndex)
        // and the request that later cancels it (ProjectEndpoints.CancelIndex) — see its own remarks.
        services.AddSingleton<ReindexCancellationRegistry>();
        services.AddSingleton<ReindexRunner>();
    }

    private static void AddContextEngine(IServiceCollection services)
    {
        services.AddSingleton<SessionDeliveryCache>();
        services.AddScoped<IContextEngine, ContextEngine>();
    }

    // Scoped like IContextEngine above: talks only to the Scoped IProjectRepository, no state to
    // share across a request.
    private static void AddProjectSettings(IServiceCollection services)
    {
        services.AddScoped<IProjectSettingsService, ProjectSettingsService>();
    }
}
