using System.Net;
using Microsoft.Extensions.AI;

namespace Context.Providers.Llm;

public sealed class OllamaChatClientDecorator(
    IChatClient inner, string model, ProviderResilienceOptions? resilience = null, TimeProvider? timeProvider = null) : DelegatingChatClient(inner)
{
    // Ollama cloud models ('-cloud') enforce hourly/daily usage limits. Once one answers 429, every
    // queued job would otherwise hit it again immediately and keep the quota exhausted, so calls
    // fail fast until the cooldown ends; each 429 after a cooldown doubles it.
    private readonly ProviderResilienceOptions _resilience = resilience ?? new();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private TimeSpan _cooldown = TimeSpan.Zero;
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        if (ActiveCooldownEnd() is { } retryAt)
        {
            throw RateLimited(retryAt);
        }

        try
        {
            var response = await base.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _cooldown = TimeSpan.Zero;
            }

            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException && IsRateLimited(ex))
        {
            throw RateLimited(StartCooldown(), ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Ollama chat request failed. Is Ollama running and has '{model}' been pulled ('ollama pull {model}')? {ex.Message}",
                ex);
        }
    }

    private DateTimeOffset? ActiveCooldownEnd()
    {
        lock (_gate)
        {
            return _time.GetUtcNow() < _cooldownUntil ? _cooldownUntil : null;
        }
    }

    private DateTimeOffset StartCooldown()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            // Concurrent requests that were already in flight all come back 429 together; only the
            // first one past the previous cooldown escalates it.
            if (now < _cooldownUntil)
            {
                return _cooldownUntil;
            }

            _cooldown = _cooldown == TimeSpan.Zero
                ? _resilience.RateLimitCooldown
                : TimeSpan.FromTicks(Math.Min(_resilience.RateLimitMaxCooldown.Ticks, _cooldown.Ticks * 2));
            _cooldownUntil = now + _cooldown;
            return _cooldownUntil;
        }
    }

    private ProviderRateLimitedException RateLimited(DateTimeOffset retryAt, Exception? inner = null) =>
        new($"Ollama rate-limited chat requests for '{model}' (429 Too Many Requests); pausing them until {retryAt:u}. " +
            "Cloud models have hourly and daily usage limits: wait for the quota to reset or switch the project to a local chat model.",
            retryAt, inner);

    private static bool IsRateLimited(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
            {
                return true;
            }
        }

        return false;
    }
}
