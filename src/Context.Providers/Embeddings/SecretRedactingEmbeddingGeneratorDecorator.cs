using Context.Providers.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Context.Providers.Embeddings;

/// <summary>
/// BACKLOG-08 / SEC-4: wraps a *cloud* embedding generator (openai) so outgoing text is scanned for
/// secret-shaped substrings (see <see cref="SecretRedactor"/>) and redacted before it leaves this
/// machine. Deliberately not applied to the Ollama/noop registrations in
/// <see cref="ServiceCollectionExtensions.AddContextProviders"/> -- those never send content
/// off-box, so there is nothing to protect against there.
/// </summary>
public sealed class SecretRedactingEmbeddingGeneratorDecorator(
    IEmbeddingGenerator<string, Embedding<float>> inner,
    ILogger<SecretRedactingEmbeddingGeneratorDecorator> logger)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default) =>
        base.GenerateAsync(RedactValues(values), options, ct);

    private IEnumerable<string> RedactValues(IEnumerable<string> values)
    {
        // No stable chunk/file identifier is available at this layer, so the batch position is the
        // most useful thing to log alongside the count -- the secret itself is never logged.
        var index = 0;
        foreach (var value in values)
        {
            var redacted = SecretRedactor.Redact(value, out var count);
            if (count > 0)
            {
                logger.LogWarning(
                    "Redacted {Count} likely secret(s) from embedding input #{Index} before sending it to an external embedding provider.",
                    count, index);
            }

            yield return redacted;
            index++;
        }
    }
}
