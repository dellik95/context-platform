using Microsoft.Extensions.AI;

namespace Context.Providers.Llm;

public sealed class FoundryLocalChatClientDecorator(IChatClient inner, Uri endpoint, string model) : DelegatingChatClient(inner)
{
    // Reports "foundry" instead of the wrapped OpenAI client's "openai".
    private readonly ChatClientMetadata _metadata = new("foundry", endpoint, model);

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IsContextLengthError(ex))
        {
            throw new InvalidOperationException(
                $"Foundry Local rejected this request as exceeding '{model}''s context window: {ex.Message} " +
                $"Pick a chat model with a larger context ('foundry model info {model}' shows its Context Length).",
                ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Foundry Local chat request failed: {ex.Message} {FoundryLocalOptions.Hint(endpoint, model)}", ex);
        }
    }

    // Foundry answers an oversized prompt with a 500: "input_ids size (N) + current sequence length (0) exceeds max length (M)".
    private static bool IsContextLengthError(Exception ex) =>
        ex.Message.Contains("exceeds max length", StringComparison.OrdinalIgnoreCase);

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(ChatClientMetadata) ? _metadata : base.GetService(serviceType, serviceKey);
}
