namespace Context.Providers.Llm;

/// <summary>
/// Configuration for <see cref="OllamaLlmProvider"/> — a local Ollama server. Separate from
/// <see cref="Context.Providers.Embeddings.OllamaOptions"/> because the chat/completion model
/// (e.g. "llama3.1", "qwen2.5-coder") is almost always a different pull than the embedding model
/// (e.g. "nomic-embed-text"), even when both point at the same Ollama instance.
/// </summary>
public sealed record OllamaChatOptions
{
    // A small, fast model: session summarization runs frequently (potentially per active session
    // on every scheduler poll - see SessionSummaryScheduler) and shares one local Ollama instance
    // with everything else, so latency compounds fast. Users who want a bigger model for quality
    // can still set CONTEXT_OLLAMA_CHAT_MODEL themselves. Exposed as a constant so Context.Host's
    // env-var fallback (Program.cs's BuildProvidersOptions) doesn't duplicate this literal.
    public const string DefaultModel = "qwen2.5-coder:1.5b";

    public string BaseUrl { get; init; } = "http://localhost:11434";

    public string Model { get; init; } = DefaultModel;
}
