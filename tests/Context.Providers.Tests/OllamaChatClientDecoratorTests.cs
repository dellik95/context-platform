using System.Net;
using Context.Providers.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Context.Providers.Tests;

public class OllamaChatClientDecoratorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_429_surfaces_as_rate_limited_with_a_one_minute_cooldown_not_a_pull_hint()
    {
        var inner = new ScriptedChatClient(RateLimit);
        var client = new OllamaChatClientDecorator(inner, "gpt-oss:20b-cloud", new ManualTimeProvider(Start));

        var act = () => client.GetResponseAsync("hi");

        var ex = (await act.Should().ThrowAsync<ProviderRateLimitedException>()).Which;
        ex.RetryAt.Should().Be(Start.AddMinutes(1));
        ex.Message.Should().Contain("429").And.NotContain("ollama pull");
    }

    [Fact]
    public async Task Calls_during_the_cooldown_fail_fast_without_reaching_Ollama()
    {
        var time = new ManualTimeProvider(Start);
        var inner = new ScriptedChatClient(RateLimit);
        var client = new OllamaChatClientDecorator(inner, "gpt-oss:20b-cloud", time);
        await Swallow(client.GetResponseAsync("hi"));

        time.Advance(TimeSpan.FromSeconds(30));
        var act = () => client.GetResponseAsync("hi");

        (await act.Should().ThrowAsync<ProviderRateLimitedException>()).Which.RetryAt.Should().Be(Start.AddMinutes(1));
        inner.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Each_429_after_a_cooldown_doubles_it_and_a_success_resets_it()
    {
        var time = new ManualTimeProvider(Start);
        var inner = new ScriptedChatClient(RateLimit, RateLimit, Ok, RateLimit);
        var client = new OllamaChatClientDecorator(inner, "gpt-oss:20b-cloud", time);

        await Swallow(client.GetResponseAsync("hi"));
        time.Advance(TimeSpan.FromMinutes(1));
        var second = await Record(client.GetResponseAsync("hi"));
        time.Advance(TimeSpan.FromMinutes(2));
        await client.GetResponseAsync("hi");
        var afterSuccess = await Record(client.GetResponseAsync("hi"));

        second!.RetryAt.Should().Be(Start.AddMinutes(1 + 2));
        afterSuccess!.RetryAt.Should().Be(Start.AddMinutes(3 + 1));
    }

    [Fact]
    public async Task Other_failures_keep_the_is_Ollama_running_hint()
    {
        var inner = new ScriptedChatClient(() => throw new HttpRequestException("refused", null, HttpStatusCode.InternalServerError));
        var client = new OllamaChatClientDecorator(inner, "llama3", new ManualTimeProvider(Start));

        var act = () => client.GetResponseAsync("hi");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("ollama pull llama3");
    }

    private static ChatResponse RateLimit() =>
        throw new HttpRequestException("Response status code does not indicate success: 429 (Too Many Requests).", null, HttpStatusCode.TooManyRequests);

    private static ChatResponse Ok() => new(new ChatMessage(ChatRole.Assistant, "ok"));

    private static async Task Swallow(Task task)
    {
        try
        {
            await task;
        }
        catch (ProviderRateLimitedException)
        {
        }
    }

    private static async Task<ProviderRateLimitedException?> Record(Task task)
    {
        try
        {
            await task;
            return null;
        }
        catch (ProviderRateLimitedException ex)
        {
            return ex;
        }
    }

    private sealed class ScriptedChatClient(params Func<ChatResponse>[] script) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var step = script[Math.Min(Calls, script.Length - 1)];
            Calls++;
            return Task.FromResult(step());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
