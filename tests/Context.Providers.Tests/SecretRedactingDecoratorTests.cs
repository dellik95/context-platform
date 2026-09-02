using Context.Providers.Embeddings;
using Context.Providers.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Providers.Tests;

/// <summary>
/// Verifies the decorators actually redact secrets in the text handed to the wrapped provider
/// (not just that <see cref="Context.Providers.Security.SecretRedactor"/> works in isolation), and
/// that ordinary content passes through unchanged.
/// </summary>
public class SecretRedactingDecoratorTests
{
    private sealed class CapturingChatClient : IChatClient
    {
        public IEnumerable<ChatMessage>? ReceivedMessages { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        {
            ReceivedMessages = messages.ToList();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class CapturingEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        public IReadOnlyList<string>? ReceivedValues { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken ct = default)
        {
            var list = values.ToList();
            ReceivedValues = list;
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                list.Select(_ => new Embedding<float>(new float[] { 0f }))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task ChatDecorator_RedactsSecretInOutgoingMessage_BeforeReachingInnerClient()
    {
        var inner = new CapturingChatClient();
        var sut = new SecretRedactingChatClientDecorator(inner, NullLogger<SecretRedactingChatClientDecorator>.Instance);
        var message = new ChatMessage(ChatRole.User, "here is my key: sk-abcdefghijklmnopqrstuvwxyz1234567890");

        await sut.GetResponseAsync([message]);

        var received = inner.ReceivedMessages!.Single();
        received.Text.Should().Contain("[REDACTED-SECRET]");
        received.Text.Should().NotContain("sk-abcdefghijklmnopqrstuvwxyz1234567890");
    }

    [Fact]
    public async Task ChatDecorator_LeavesOrdinaryMessageUnchanged()
    {
        var inner = new CapturingChatClient();
        var sut = new SecretRedactingChatClientDecorator(inner, NullLogger<SecretRedactingChatClientDecorator>.Instance);
        var message = new ChatMessage(ChatRole.User, "please summarize this file's public API surface");

        await sut.GetResponseAsync([message]);

        inner.ReceivedMessages!.Single().Text.Should().Be("please summarize this file's public API surface");
    }

    [Fact]
    public async Task EmbeddingDecorator_RedactsSecretInOutgoingValue_BeforeReachingInnerGenerator()
    {
        var inner = new CapturingEmbeddingGenerator();
        var sut = new SecretRedactingEmbeddingGeneratorDecorator(inner, NullLogger<SecretRedactingEmbeddingGeneratorDecorator>.Instance);

        await sut.GenerateAsync(["ghp_1234567890ABCDEFGHIJabcdefghijklmnop"]);

        inner.ReceivedValues!.Single().Should().Contain("[REDACTED-SECRET]");
        inner.ReceivedValues!.Single().Should().NotContain("ghp_1234567890ABCDEFGHIJabcdefghijklmnop");
    }

    [Fact]
    public async Task EmbeddingDecorator_LeavesOrdinaryContentUnchanged()
    {
        var inner = new CapturingEmbeddingGenerator();
        var sut = new SecretRedactingEmbeddingGeneratorDecorator(inner, NullLogger<SecretRedactingEmbeddingGeneratorDecorator>.Instance);

        await sut.GenerateAsync(["public class Foo { public void Bar() {} }"]);

        inner.ReceivedValues!.Single().Should().Be("public class Foo { public void Bar() {} }");
    }
}
