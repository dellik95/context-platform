using Microsoft.Extensions.AI;

namespace Context.Providers.Embeddings;

/// <summary>
/// Turns Ollama's "context length exceeded" rejection into an actionable error instead of retrying
/// with the prompt silently halved -- a truncated prompt produces an embedding for only part of the
/// chunk's content while still getting stored as if it represented the whole thing, corrupting
/// search results in a way that's invisible until someone notices bad matches. Failing loudly here
/// surfaces as a Failed job (see EmbedChunkJobHandler/the System tab's Failed jobs table) that names
/// the actual chunk, instead of a silent quality regression nobody gets to see.
/// </summary>
public sealed class OllamaEmbeddingGeneratorDecorator(IEmbeddingGenerator<string, Embedding<float>> inner, string model)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    // Ollama serializes inference per model on one CPU/GPU pipeline by default -- JobDispatcher's
    // own worker pool (one per CPU core, so a dozen-plus on a typical dev box) can fire far more
    // concurrent EmbedChunk requests than a single local Ollama instance can actually run at once.
    // The excess doesn't fail fast, it queues up *inside* Ollama; by the time a late request's turn
    // comes up it can exceed the caller's own HttpClient timeout, and every one of those requests
    // holds its job's DB scope open the whole time it waits, which was observed degrading latency
    // for everything else sharing the same SQLite database (hooks, search) while a large backlog
    // drained. Process-wide (not per-model/per-project) because every Ollama-backed project on this
    // host ultimately shares the same one local Ollama process -- the real bottleneck is that
    // shared backend, not any one project's config. Capping it here means the requests that don't
    // fit just wait cheaply (no HTTP call, no open DB scope) instead of timing out.
    private const int MaxConcurrentRequests = 2;
    private static readonly SemaphoreSlim Gate = new(MaxConcurrentRequests, MaxConcurrentRequests);

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await base.GenerateAsync(values, options, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IsContextLengthError(ex))
        {
            throw new InvalidOperationException(
                $"Ollama embeddings rejected this content as exceeding '{model}''s context window. " +
                "It needs to be split into smaller chunks upstream, or embedded with a model that has a larger window.",
                ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static bool IsContextLengthError(Exception ex) =>
        ex.Message.Contains("context length", StringComparison.OrdinalIgnoreCase);
}
