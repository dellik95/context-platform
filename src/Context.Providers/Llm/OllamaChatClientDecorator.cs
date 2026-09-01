using Microsoft.Extensions.AI;

namespace Context.Providers.Llm;

public sealed class OllamaChatClientDecorator(IChatClient inner, string model) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Ollama chat request failed. Is Ollama running and has '{model}' been pulled ('ollama pull {model}')? {ex.Message}",
                ex);
        }
    }
}
