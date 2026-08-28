using Context.Graph.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Context.Graph;

/// <summary>
/// DI wiring for Context.Graph. Registers the per-project in-memory cache (as a singleton
/// keyed/dictionary-backed service, see <see cref="GraphCacheProvider"/>) and the graph
/// read/write services. The caller must separately register a concrete
/// <see cref="IGraphPersistence"/> — Context.Application wires one backed by
/// Context.Infrastructure's graph repository at composition time.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddContextGraph(this IServiceCollection services)
    {
        services.AddSingleton<IGraphCacheProvider, GraphCacheProvider>();
        services.AddSingleton<IGraphStore, GraphStore>();
        services.AddSingleton<IGraphTraversal, GraphTraversal>();

        return services;
    }
}
