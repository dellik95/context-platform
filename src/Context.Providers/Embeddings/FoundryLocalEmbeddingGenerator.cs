using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace Context.Providers.Embeddings;

/// <summary>
/// Embeddings from Foundry Local's OpenAI-compatible <c>/v1/embeddings</c>. Hand-rolled instead of
/// the OpenAI SDK's <c>EmbeddingClient</c>, which always requests base64-encoded vectors; this asks
/// for plain floats, the encoding every OpenAI-compatible server supports.
/// </summary>
public sealed class FoundryLocalEmbeddingGenerator(HttpClient http, string model) : IEmbeddingGenerator<string, Embedding<float>>
{
    // Same reason as OllamaEmbeddingGeneratorDecorator's gate: one local inference process can't run
    // JobDispatcher's whole worker pool at once, so the excess waits here rather than timing out inside it.
    private static readonly SemaphoreSlim Gate = new(2, 2);
    private static readonly TimeSpan InteractiveTimeout = TimeSpan.FromSeconds(45);

    private readonly EmbeddingGeneratorMetadata _metadata = new("foundry", http.BaseAddress, model);

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var inputs = values.ToArray();
        var isInteractive = options?.AdditionalProperties?.ContainsKey(OllamaEmbeddingGeneratorDecorator.InteractiveRequestKey) == true;
        if (!isInteractive)
        {
            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        using var timeoutCts = isInteractive ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        timeoutCts?.CancelAfter(InteractiveTimeout);
        var ct = timeoutCts?.Token ?? cancellationToken;

        try
        {
            var request = new EmbeddingsRequest(model, inputs.Length == 1 ? inputs[0] : inputs, "float");
            using var response = await http.PostAsJsonAsync("embeddings", request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"Foundry Local embeddings returned {(int)response.StatusCode}: {body} {FoundryLocalOptions.Hint(http.BaseAddress, model)}");
            }

            var payload = await response.Content.ReadFromJsonAsync<EmbeddingsResponse>(ct).ConfigureAwait(false);
            if (payload?.Data is not { } data || data.Count != inputs.Length)
            {
                throw new InvalidOperationException(
                    $"Foundry Local returned {payload?.Data?.Count ?? 0} embeddings for {inputs.Length} inputs.");
            }

            return new GeneratedEmbeddings<Embedding<float>>(
                data.OrderBy(d => d.Index).Select(d => new Embedding<float>(d.Embedding) { ModelId = model }))
            {
                Usage = payload.Usage is { } usage
                    ? new UsageDetails { InputTokenCount = usage.PromptTokens, TotalTokenCount = usage.TotalTokens }
                    : null,
            };
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Foundry Local embeddings request failed: {ex.Message} {FoundryLocalOptions.Hint(http.BaseAddress, model)}", ex);
        }
        catch (OperationCanceledException) when (isInteractive && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Foundry Local embeddings ('{model}') did not respond within {InteractiveTimeout.TotalSeconds:0}s for this interactive request. " +
                FoundryLocalOptions.Hint(http.BaseAddress, model));
        }
        finally
        {
            if (!isInteractive)
            {
                Gate.Release();
            }
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is not null ? null
        : serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata
        : serviceType.IsInstanceOfType(this) ? this
        : null;

    public void Dispose()
    {
    }

    private sealed record EmbeddingsRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] object Input,
        [property: JsonPropertyName("encoding_format")] string EncodingFormat);

    private sealed record EmbeddingsResponse(
        [property: JsonPropertyName("data")] IReadOnlyList<EmbeddingData>? Data,
        [property: JsonPropertyName("usage")] EmbeddingUsage? Usage);

    private sealed record EmbeddingData(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);

    private sealed record EmbeddingUsage(
        [property: JsonPropertyName("prompt_tokens")] long? PromptTokens,
        [property: JsonPropertyName("total_tokens")] long? TotalTokens);
}
