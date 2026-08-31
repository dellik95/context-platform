namespace Context.Providers.Embeddings;

/// <summary>Configuration for <see cref="OllamaEmbeddingProvider"/> — a local Ollama server.</summary>
public sealed record OllamaOptions
{
    public string BaseUrl { get; init; } = "http://localhost:11434";

    public string Model { get; init; } = "embeddinggemma";

    /// <summary>Optional known output dimensionality; discovered from the first response if unset.</summary>
    public int? Dimensions { get; init; }
}
