using System.Net;
using System.Text;
using System.Text.Json;
using Context.Providers.Embeddings;
using Context.Providers.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Providers.Tests;

public class FoundryLocalProviderTests
{
    [Fact]
    public async Task Embeddings_post_float_encoding_to_the_embeddings_endpoint_and_parse_the_vector()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"data":[{"index":0,"embedding":[0.5,-1.25,2]}],"usage":{"prompt_tokens":3,"total_tokens":3}}""");
        using var generator = CreateGenerator(handler);

        var result = await generator.GenerateAsync(["hello"]);

        handler.RequestUri.Should().Be(new Uri("http://127.0.0.1:5273/v1/embeddings"));
        using var body = JsonDocument.Parse(handler.RequestBody!);
        body.RootElement.GetProperty("model").GetString().Should().Be("qwen3-embedding-0.6b");
        body.RootElement.GetProperty("input").GetString().Should().Be("hello");
        body.RootElement.GetProperty("encoding_format").GetString().Should().Be("float");
        result.Should().ContainSingle();
        result[0].Vector.ToArray().Should().Equal(0.5f, -1.25f, 2f);
        result[0].ModelId.Should().Be("qwen3-embedding-0.6b");
        result.Usage!.InputTokenCount.Should().Be(3);
    }

    [Fact]
    public async Task Embeddings_for_several_inputs_are_returned_in_input_order()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"data":[{"index":1,"embedding":[2]},{"index":0,"embedding":[1]}]}""");
        using var generator = CreateGenerator(handler);

        var result = await generator.GenerateAsync(["first", "second"]);

        using var body = JsonDocument.Parse(handler.RequestBody!);
        body.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()).Should().Equal("first", "second");
        result.Select(e => e.Vector.Span[0]).Should().Equal(1f, 2f);
    }

    [Fact]
    public async Task A_model_that_is_not_loaded_fails_with_a_hint_to_load_it()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, """{"error":"Model 'qwen3-embedding-0.6b' is not loaded"}""");
        using var generator = CreateGenerator(handler);

        var act = () => generator.GenerateAsync(["hello"]);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("400").And.Contain("is not loaded").And.Contain("foundry model load qwen3-embedding-0.6b");
    }

    [Fact]
    public void Embedding_generator_reports_foundry_as_its_provider()
    {
        using var generator = CreateGenerator(new StubHandler(HttpStatusCode.OK, "{}"));

        var metadata = generator.GetService<EmbeddingGeneratorMetadata>();

        metadata!.ProviderName.Should().Be("foundry");
        metadata.DefaultModelId.Should().Be("qwen3-embedding-0.6b");
    }

    [Fact]
    public void Chat_client_reports_foundry_as_its_provider()
    {
        using var client = ProviderFactory.CreateFoundryLocalChatClient("http://127.0.0.1:5273/v1/", "phi-3.5-mini", TimeSpan.FromSeconds(30));

        var metadata = client.GetService<ChatClientMetadata>();

        metadata!.ProviderName.Should().Be("foundry");
        metadata.DefaultModelId.Should().Be("phi-3.5-mini");
        metadata.ProviderUri.Should().Be(new Uri("http://127.0.0.1:5273/v1"));
    }

    [Fact]
    public async Task Chat_failures_carry_the_foundry_hint()
    {
        using var client = new FoundryLocalChatClientDecorator(new ThrowingChatClient(), new Uri("http://127.0.0.1:5273/v1"), "phi-3.5-mini");

        var act = () => client.GetResponseAsync("hi");

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("connection refused").And.Contain("foundry model load phi-3.5-mini");
    }

    [Fact]
    public void AddContextProviders_registers_foundry_for_both_embeddings_and_chat_without_secret_redaction()
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddContextProviders(new ContextProvidersOptions { EmbeddingProvider = "foundry", LlmProvider = "foundry" });
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>().Should().BeOfType<FoundryLocalEmbeddingGenerator>();
        provider.GetRequiredService<IChatClient>().Should().BeOfType<FoundryLocalChatClientDecorator>();
    }

    private static FoundryLocalEmbeddingGenerator CreateGenerator(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:5273/v1/") }, "qwen3-embedding-0.6b");

    private sealed class StubHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("connection refused");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
