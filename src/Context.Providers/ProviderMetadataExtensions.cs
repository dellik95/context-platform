using Context.Providers.Embeddings;
using Microsoft.Extensions.AI;

namespace Context.Providers;

public static class ProviderMetadataExtensions
{
    public static string GetProviderName(this IEmbeddingGenerator<string, Embedding<float>> generator) =>
        generator.GetService<EmbeddingGeneratorMetadata>()?.ProviderName ?? "unknown";

    public static string? GetModelId(this IEmbeddingGenerator<string, Embedding<float>> generator) =>
        generator.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId;

    /// <summary>
    /// Cache/vector-index partition key. Bare <see cref="GetProviderName"/> alone collides across
    /// different models under the same provider (e.g. switching Ollama's embedding model from
    /// 768-dim embeddinggemma to 4096-dim qwen3-embedding still reports ProviderName "ollama"),
    /// which corrupts the content-hash embedding cache and crashes sqlite-vec inserts with a
    /// dimension mismatch against the table an earlier model already created. Including the model
    /// id gives each (provider, model) pair its own cache partition and its own
    /// <c>chunk_vectors_*</c> table instead.
    /// </summary>
    public static string GetProviderModelKey(this IEmbeddingGenerator<string, Embedding<float>> generator)
    {
        var providerName = generator.GetProviderName();
        var modelId = generator.GetModelId();
        return modelId is { Length: > 0 } ? $"{providerName}:{modelId}" : providerName;
    }

    /// <summary>What one request to <paramref name="generator"/> may carry; a generator not wrapped in
    /// <see cref="BatchingEmbeddingGenerator"/> gets one input per request.</summary>
    public static EmbeddingBatchLimits GetBatchLimits(this IEmbeddingGenerator<string, Embedding<float>> generator) =>
        generator.GetService<EmbeddingBatchLimits>() ?? EmbeddingBatchLimits.None;

    public static string GetProviderName(this IChatClient client) =>
        client.GetService<ChatClientMetadata>()?.ProviderName ?? "unknown";

    public static string? GetModelId(this IChatClient client) =>
        client.GetService<ChatClientMetadata>()?.DefaultModelId;

    public static bool IsNoOp(this IChatClient client) =>
        string.Equals(client.GetProviderName(), "noop", StringComparison.OrdinalIgnoreCase);
}
