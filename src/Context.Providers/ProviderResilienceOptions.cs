using System.ClientModel.Primitives;
using System.Net;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Context.Providers;

/// <summary>
/// How hard a provider client pushes back on failure. Defaults are deliberately gentle: a failed LLM
/// call is not retried (the job-level retry in JobDispatcher already covers it), and when retries
/// are enabled they back off exponentially so a struggling endpoint isn't hammered.
/// </summary>
public sealed record ProviderResilienceOptions
{
    public const int DefaultRetryBaseDelaySeconds = 30;
    public const int DefaultRateLimitCooldownMinutes = 5;
    public const int DefaultRateLimitMaxCooldownMinutes = 60;

    /// <summary>Budget for one provider call, including any retries and their backoff.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(ContextProvidersOptions.DefaultLlmTimeoutSeconds);

    /// <summary>0 = never retry. Only connection failures, 408 and 5xx are retried; a 429 or a
    /// timeout never is, since re-sending the same prompt then only adds load.</summary>
    public int MaxRetries { get; init; }

    /// <summary>First retry delay; each further retry doubles it, with jitter.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromSeconds(DefaultRetryBaseDelaySeconds);

    /// <summary>How long Ollama chat calls pause after a 429; doubles per repeat, capped at <see cref="RateLimitMaxCooldown"/>.</summary>
    public TimeSpan RateLimitCooldown { get; init; } = TimeSpan.FromMinutes(DefaultRateLimitCooldownMinutes);

    public TimeSpan RateLimitMaxCooldown { get; init; } = TimeSpan.FromMinutes(DefaultRateLimitMaxCooldownMinutes);

    internal HttpRetryStrategyOptions ToHttpRetryStrategy() => new()
    {
        MaxRetryAttempts = MaxRetries,
        Delay = RetryBaseDelay,
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        ShouldHandle = static args => ValueTask.FromResult(IsRetryable(args.Outcome)),
    };

    internal HttpMessageHandler WithRetry(HttpMessageHandler inner)
    {
        if (MaxRetries <= 0)
        {
            return inner;
        }

        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>().AddRetry(ToHttpRetryStrategy()).Build();
        return new ResilienceHandler(pipeline) { InnerHandler = inner };
    }

    // The OpenAI SDK retries 3 times on its own by default; this makes it follow the same settings.
    internal ClientRetryPolicy ToClientRetryPolicy() => new BackoffClientRetryPolicy(MaxRetries, RetryBaseDelay);

    private static bool IsRetryable(Outcome<HttpResponseMessage> outcome) => outcome.Result is { } response
        ? response.StatusCode == HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500
        : outcome.Exception is HttpRequestException;

    private sealed class BackoffClientRetryPolicy(int maxRetries, TimeSpan baseDelay) : ClientRetryPolicy(Math.Max(0, maxRetries))
    {
        protected override TimeSpan GetNextDelay(PipelineMessage message, int tryCount) =>
            TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * Math.Pow(2, tryCount - 1) * (0.8 + Random.Shared.NextDouble() * 0.4));
    }
}
