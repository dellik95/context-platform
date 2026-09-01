using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using Context.Memory.Summarization;
using Microsoft.Extensions.DependencyInjection;

namespace Context.Memory;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISessionSummarizer"/> and <see cref="IMemoryExtractor"/>.
    /// Callers must separately register <c>IChatClient</c>/<c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>
    /// (e.g. via <c>Context.Providers.AddContextProviders</c>) and the
    /// <see cref="ISessionEventSource"/> / <see cref="IMemoryStore"/> seam implementations
    /// (Context.Application wires these to the real repositories).
    /// </summary>
    public static IServiceCollection AddContextMemory(this IServiceCollection services)
    {
        services.AddScoped<ISessionSummarizer, LlmSessionSummarizer>();
        services.AddScoped<IMemoryExtractor, LlmMemoryExtractor>();
        return services;
    }
}
