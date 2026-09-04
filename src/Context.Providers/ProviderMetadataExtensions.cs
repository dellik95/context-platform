using Microsoft.Extensions.AI;

namespace Context.Providers;

public static class ProviderMetadataExtensions
{
    public static string GetProviderName(this IEmbeddingGenerator<string, Embedding<float>> generator) =>
        generator.GetService<EmbeddingGeneratorMetadata>()?.ProviderName ?? "unknown";

    public static string GetProviderName(this IChatClient client) =>
        client.GetService<ChatClientMetadata>()?.ProviderName ?? "unknown";

    public static string? GetModelId(this IChatClient client) =>
        client.GetService<ChatClientMetadata>()?.DefaultModelId;

    public static bool IsNoOp(this IChatClient client) =>
        string.Equals(client.GetProviderName(), "noop", StringComparison.OrdinalIgnoreCase);
}
