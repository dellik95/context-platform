namespace Context.Providers;

/// <summary>
/// Configuration for a local Ollama server. Registered as two <em>named</em> options instances
/// (<see cref="EmbeddingName"/>/<see cref="ChatName"/>, configured by <see cref="OllamaOptionsSetup"/>)
/// rather than two near-duplicate classes, because the embedding and chat models are almost always
/// different pulls on the same Ollama instance (e.g. "embeddinggemma" vs. "qwen2.5-coder:1.5b").
/// </summary>
public sealed class OllamaOptions
{
    public const string EmbeddingName = "embedding";
    public const string ChatName = "chat";

    public const string DefaultEmbeddingModel = "embeddinggemma";

    // A small, fast model: session summarization runs frequently (potentially per active session
    // on every scheduler poll - see SessionSummaryScheduler) and shares one local Ollama instance
    // with everything else, so latency compounds fast. Users who want a bigger model for quality
    // can still set CONTEXT_OLLAMA_CHAT_MODEL themselves.
    public const string DefaultChatModel = "qwen2.5-coder:1.5b";

    public string BaseUrl { get; set; } = "http://localhost:11434";

    public string Model { get; set; } = "";

    /// <summary>Optional known output dimensionality (embedding only); discovered from the first response if unset.</summary>
    public int? Dimensions { get; set; }
}
