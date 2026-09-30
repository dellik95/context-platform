namespace Context.Providers.Embeddings;

/// <summary>
/// An embedding input exceeded the model's context window. Specific to the input rather than the
/// provider, so a caller embedding a batch can retry the other inputs on their own.
/// </summary>
public sealed class EmbeddingContextLengthException(string message, Exception innerException)
    : InvalidOperationException(message, innerException)
{
    // Ollama ("the input length exceeds the context length") and OpenAI ("maximum context length is
    // 8192 tokens") both phrase it this way.
    internal static bool IsContextLengthError(Exception ex) =>
        ex.Message.Contains("context length", StringComparison.OrdinalIgnoreCase);
}
