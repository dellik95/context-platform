namespace Context.Providers.Embeddings;

/// <summary>
/// Configuration for <see cref="OpenAiEmbeddingProvider"/>. Populate <see cref="ApiKey"/> from a
/// secret store or environment variable (e.g. OPENAI_API_KEY) — never hardcode it.
/// </summary>
public sealed record OpenAiOptions
{
    public string? ApiKey { get; init; }

    public string Model { get; init; } = "text-embedding-3-small";

    public string BaseUrl { get; init; } = "https://api.openai.com/v1";
}
