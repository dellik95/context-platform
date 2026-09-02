using Context.Providers.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Context.Providers.Llm;

/// <summary>
/// BACKLOG-08 / SEC-4: wraps a *cloud* chat client (anthropic) so outgoing message text is scanned
/// for secret-shaped substrings (see <see cref="SecretRedactor"/>) and redacted before it leaves
/// this machine. Deliberately not applied to the Ollama/noop registrations in
/// <see cref="ServiceCollectionExtensions.AddContextProviders"/> -- those never send content
/// off-box, so there is nothing to protect against there.
/// </summary>
public sealed class SecretRedactingChatClientDecorator(IChatClient inner, ILogger<SecretRedactingChatClientDecorator> logger)
    : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
        base.GetResponseAsync(RedactMessages(messages), options, ct);

    private IEnumerable<ChatMessage> RedactMessages(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            yield return RedactMessage(message);
        }
    }

    private ChatMessage RedactMessage(ChatMessage message)
    {
        var redactedAny = false;
        var contents = new List<AIContent>(message.Contents.Count);
        foreach (var content in message.Contents)
        {
            if (content is TextContent text)
            {
                var redacted = SecretRedactor.Redact(text.Text, out var count);
                if (count > 0)
                {
                    redactedAny = true;
                    contents.Add(new TextContent(redacted));
                    continue;
                }
            }

            contents.Add(content);
        }

        if (!redactedAny)
        {
            return message;
        }

        // No stable chunk/file identifier is available at this layer (the decorator only sees the
        // final chat message, not indexing metadata), so the role is the most useful thing to log
        // alongside the count -- the secret itself is never logged.
        logger.LogWarning(
            "Redacted likely secret(s) from an outgoing chat message (role: {Role}) before sending it to an external LLM provider.",
            message.Role);
        return new ChatMessage(message.Role, contents);
    }
}
