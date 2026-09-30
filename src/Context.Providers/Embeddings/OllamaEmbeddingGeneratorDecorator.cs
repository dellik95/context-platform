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

    /// <summary>
    /// Set on <see cref="EmbeddingGenerationOptions.AdditionalProperties"/> (to any value) by a
    /// caller embedding a live user query (<c>EmbeddingLookupAdapter.EmbedAsync</c>, the only path
    /// <c>HybridSearchEngine</c> uses) to skip <see cref="Gate"/> entirely. A single interactive
    /// query is infrequent and bounded -- unlike the sustained, high-volume EmbedChunk backlog the
    /// gate exists to tame, so it's cheaper to let it through immediately than to make a user's
    /// search sit in line behind thousands of background embedding calls it has nothing to do with.
    /// </summary>
    public const string InteractiveRequestKey = "ContextPlatform.InteractiveEmbeddingRequest";

    // Bounds ONLY an interactive call's real HTTP request -- background EmbedChunk calls keep
    // relying on the caller's own timeout/retry (they can be re-enqueued; a hung one just occupies
    // a Gate permit). An interactive caller (memory_remember, live search) has no retry and is
    // already skipping the Gate to avoid queuing behind background work, so a genuinely-unreachable
    // Ollama host must fail fast instead of hanging for however long the OS-level TCP connect
    // timeout or the shared "llm-retry" resilience pipeline's own (much longer) timeout takes --
    // both of which were observed exceeding the ~60s ambient cutoff an MCP tool call gets, which is
    // exactly the ConsolidateAsync-embedding hang that motivated this. Long enough that a real but
    // slow local Ollama (e.g. still loading the model into memory) has room to actually answer.
    private static readonly TimeSpan InteractiveTimeout = TimeSpan.FromSeconds(45);

    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default)
    {
        var isInteractive = options?.AdditionalProperties?.ContainsKey(InteractiveRequestKey) == true;
        if (!isInteractive)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
        }

        using var timeoutCts = isInteractive ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        timeoutCts?.CancelAfter(InteractiveTimeout);
        var effectiveCt = timeoutCts?.Token ?? ct;

        try
        {
            return await base.GenerateAsync(values, options, effectiveCt).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && EmbeddingContextLengthException.IsContextLengthError(ex))
        {
            throw new EmbeddingContextLengthException(
                $"Ollama embeddings rejected this content as exceeding '{model}''s context window. " +
                "It needs to be split into smaller chunks upstream, or embedded with a model that has a larger window.",
                ex);
        }
        catch (OperationCanceledException) when (isInteractive && !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Ollama embeddings ('{model}') did not respond within {InteractiveTimeout.TotalSeconds:0}s for this interactive request. " +
                "The configured Ollama host is likely unreachable.");
        }
        finally
        {
            if (!isInteractive)
            {
                Gate.Release();
            }
        }
    }
}
