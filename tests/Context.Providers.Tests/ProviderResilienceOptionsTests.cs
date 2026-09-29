using System.Net;
using FluentAssertions;

namespace Context.Providers.Tests;

public class ProviderResilienceOptionsTests
{
    [Fact]
    public async Task Retries_are_off_by_default()
    {
        var handler = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        var response = await Send(new ProviderResilienceOptions(), handler);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_5xx_is_retried_up_to_the_configured_count()
    {
        var handler = new ScriptedHandler(HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);

        var response = await Send(Retrying(maxRetries: 2), handler);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        handler.Calls.Should().Be(3);
    }

    [Fact]
    public async Task A_429_is_never_retried()
    {
        var handler = new ScriptedHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);

        var response = await Send(Retrying(maxRetries: 3), handler);

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        handler.Calls.Should().Be(1);
    }

    private static ProviderResilienceOptions Retrying(int maxRetries) =>
        new() { MaxRetries = maxRetries, RetryBaseDelay = TimeSpan.FromMilliseconds(1) };

    private static async Task<HttpResponseMessage> Send(ProviderResilienceOptions resilience, HttpMessageHandler inner)
    {
        using var client = new HttpClient(resilience.WithRetry(inner));
        return await client.GetAsync("http://localhost/api/chat");
    }

    private sealed class ScriptedHandler(params HttpStatusCode[] script) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var status = script[Math.Min(Calls, script.Length - 1)];
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
