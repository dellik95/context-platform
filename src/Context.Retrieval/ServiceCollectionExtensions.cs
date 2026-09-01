using Context.Retrieval.Abstractions;
using Context.Retrieval.Scoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Context.Retrieval;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the scoring/merge/rerank/budget pipeline against whatever <see cref="HybridScoringOptions"/>/
    /// <see cref="HybridSearchOptions"/>/<see cref="ContextPackOptions"/> are already configured in
    /// <paramref name="services"/> (each options class's own property initializers supply the
    /// default when nothing configures it) -- bind real config via
    /// <c>services.Configure&lt;HybridScoringOptions&gt;(configuration.GetSection(...))</c> etc.
    /// (see Context.Host's Program.cs) before calling this.
    /// </summary>
    public static IServiceCollection AddContextRetrieval(this IServiceCollection services)
    {
        services.AddOptions<HybridScoringOptions>();
        services.AddOptions<HybridSearchOptions>();
        services.AddOptions<ContextPackOptions>();

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
