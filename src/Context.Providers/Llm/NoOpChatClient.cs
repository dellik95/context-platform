using Microsoft.Extensions.AI;

namespace Context.Providers.Llm;

/// <summary>
/// Deterministic, templated response with no network call, so session summarization and memory
/// extraction keep working end-to-end (at lower quality than a real model) when no LLM is configured.
/// </summary>
public sealed class NoOpChatClient : IChatClient
{
    private const int MaxSystemPreview = 200;
    private const int MaxUserPreview = 1200;

    private static readonly ChatClientMetadata Metadata = new("noop", null, "noop");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var systemPrompt = messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Text;
        var userPrompt = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text;

        var text =
            "[No LLM provider configured — deterministic offline summary]" + Environment.NewLine +
            "System guidance: " + Truncate(systemPrompt, MaxSystemPreview) + Environment.NewLine +
            "Request:" + Environment.NewLine + Truncate(userPrompt, MaxUserPreview) + Environment.NewLine +
            "(Configure an LLM provider, e.g. Anthropic, for higher-quality summarization.)";

        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await GetResponseAsync(messages, options, ct).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? Metadata : null;

    public void Dispose()
    {
    }

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "(none)";
        }

        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}
