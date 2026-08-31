using Context.Retrieval.Abstractions;
using Context.Retrieval.Scoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Context.Retrieval;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddContextRetrieval(
        this IServiceCollection services,
        HybridScoringOptions? scoringOptions = null,
        HybridSearchOptions? searchOptions = null,
        ContextPackOptions? packOptions = null)
    {
        var scoring = scoringOptions ?? new HybridScoringOptions();
        var search = searchOptions ?? new HybridSearchOptions();
        var pack = packOptions ?? new ContextPackOptions();

        services.AddOptions<HybridScoringOptions>().Configure(o =>
        {
            o.LexicalWeight = scoring.LexicalWeight;
            o.VectorWeight = scoring.VectorWeight;
            o.GraphWeight = scoring.GraphWeight;
            o.RecencyWeight = scoring.RecencyWeight;
            o.MemoryWeight = scoring.MemoryWeight;
        });

        services.AddOptions<HybridSearchOptions>().Configure(o =>
        {
            o.GraphExpansionSeedCount = search.GraphExpansionSeedCount;
            o.GraphExpansionHops = search.GraphExpansionHops;
            o.RerankTopK = search.RerankTopK;
            o.SemanticBoostEnabled = search.SemanticBoostEnabled;
            o.SemanticStrongMatchThreshold = search.SemanticStrongMatchThreshold;
            o.SemanticBoostFactor = search.SemanticBoostFactor;
        });

        services.AddOptions<ContextPackOptions>().Configure(o =>
        {
            o.ArchitectureReserveRatio = pack.ArchitectureReserveRatio;
            o.PrimaryReserveRatio = pack.PrimaryReserveRatio;
            o.HistoricalReserveRatio = pack.HistoricalReserveRatio;
            o.OpenIssuesReserveRatio = pack.OpenIssuesReserveRatio;
            o.SafetyReserveRatio = pack.SafetyReserveRatio;
        });

        services.AddSingleton<IHybridScorer>(sp =>
            new HybridScorer(sp.GetRequiredService<IOptionsMonitor<HybridScoringOptions>>()));

        services.AddSingleton<ISearchEngine>(sp =>
            new HybridSearchEngine(
                sp.GetRequiredService<ISearchIndex>(),
                sp.GetRequiredService<IEmbeddingLookup>(),
                sp.GetRequiredService<Context.Graph.Abstractions.IGraphTraversal>(),
                sp.GetRequiredService<IReranker>(),
                sp.GetRequiredService<IHybridScorer>(),
                sp.GetRequiredService<IOptionsMonitor<HybridSearchOptions>>()));

        services.AddSingleton<IContextPackBuilder>(sp =>
            new ContextPackBuilder(
                sp.GetRequiredService<ISearchEngine>(),
                sp.GetRequiredService<IOptionsMonitor<ContextPackOptions>>().CurrentValue));

        return services;
    }
}
