namespace Context.Providers.Llm;

/// <summary>
/// Configuration for <see cref="OllamaLlmProvider"/> — a local Ollama server. Separate from
/// <see cref="Context.Providers.Embeddings.OllamaOptions"/> because the chat/completion model
/// (e.g. "llama3.1", "qwen2.5-coder") is almost always a different pull than the embedding model
/// (e.g. "nomic-embed-text"), even when both point at the same Ollama instance.
/// </summary>
public sealed record OllamaChatOptions
{
    public string BaseUrl { get; init; } = "http://localhost:11434";

    public string Model { get; init; } = "llama3.1";
}
