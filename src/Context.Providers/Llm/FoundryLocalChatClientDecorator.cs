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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Foundry Local chat request failed: {ex.Message} {FoundryLocalOptions.Hint(endpoint, model)}", ex);
        }
    }

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(ChatClientMetadata) ? _metadata : base.GetService(serviceType, serviceKey);
}
