using Context.Retrieval.Abstractions;
using Context.Retrieval.Scoring;
using Microsoft.Extensions.DependencyInjection;

namespace Context.Retrieval;

/// <summary>
/// DI wiring for Context.Retrieval. Registers the scoring/merge/rerank/budget logic
/// (<see cref="HybridScorer"/>, <see cref="HybridSearchEngine"/>, <see cref="ContextPackBuilder"/>)
/// with their default option objects. The caller (Context.Application) must separately register
/// the persistence-facing seams this project does not implement: <see cref="ISearchIndex"/>
/// (backed by Context.Infrastructure + SQLite FTS5), <see cref="IEmbeddingLookup"/> and
/// <see cref="IReranker"/> (adapters over the real Context.Providers implementations).
/// <see cref="Context.Graph.Abstractions.IGraphTraversal"/> is expected to already be registered
/// via <c>AddContextGraph</c>.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddContextRetrieval(
        this IServiceCollection services,
        HybridScoringOptions? scoringOptions = null,
        HybridSearchOptions? searchOptions = null,
        ContextPackOptions? packOptions = null)
    {
        services.AddSingleton(scoringOptions ?? new HybridScoringOptions());
        services.AddSingleton(searchOptions ?? new HybridSearchOptions());
        services.AddSingleton(packOptions ?? new ContextPackOptions());

        services.AddSingleton<IHybridScorer, HybridScorer>();
        services.AddSingleton<ISearchEngine, HybridSearchEngine>();
        services.AddSingleton<IContextPackBuilder, ContextPackBuilder>();

        return services;
    }
}
