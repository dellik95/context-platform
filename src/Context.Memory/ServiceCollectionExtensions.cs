using Context.Memory.Abstractions;
using Context.Memory.Consolidation;
using Context.Memory.Extraction;
using Context.Memory.Summarization;
using Microsoft.Extensions.DependencyInjection;

namespace Context.Memory;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="ISessionSummarizer"/>, <see cref="IMemoryConsolidator"/>, and
    /// <see cref="IMemoryExtractor"/>.
    /// Callers must separately register <c>IChatClient</c>/<c>IEmbeddingGenerator&lt;string, Embedding&lt;float&gt;&gt;</c>
    /// (e.g. via <c>Context.Providers.AddContextProviders</c>) and the <see cref="ISessionEventSource"/>,
    /// <see cref="IMemoryStore"/>, <see cref="IUsageRecorder"/> and <see cref="ICodeVerifier"/> seam
    /// implementations (Context.Application wires these to the real repositories).
    /// </summary>
    public static IServiceCollection AddContextMemory(this IServiceCollection services)
    {
        services.AddScoped<ISessionSummarizer, LlmSessionSummarizer>();
        services.AddScoped<IMemoryConsolidator, MemoryConsolidator>();
        services.AddScoped<MemoryReconsolidator>();
        services.AddScoped<IMemoryExtractor, LlmMemoryExtractor>();
        return services;
    }
}
