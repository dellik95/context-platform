using Context.Providers.Embeddings;
using Context.Providers.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Providers.Tests;

/// <summary>
/// BACKLOG-08 / SEC-4: proves <see cref="ServiceCollectionExtensions.AddContextProviders"/> only
/// wraps the *cloud* provider registrations (openai/anthropic) with secret redaction, and leaves
/// ollama/noop untouched -- those never send content off this machine, so there's nothing for the
/// redactor to protect there. Only object construction is exercised (no network call is made by
/// building an OpenAI/Anthropic client), so this runs offline like the rest of the suite.
/// </summary>
public class ServiceCollectionExtensionsSecretRedactionTests
{
    private static ServiceProvider BuildProvider(Context.Providers.ContextProvidersOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddContextProviders(options);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void OpenAiEmbeddingProvider_IsWrappedWithSecretRedaction()
    {
        using var provider = BuildProvider(new Context.Providers.ContextProvidersOptions
        {
            EmbeddingProvider = "openai",
            OpenAiApiKey = "sk-test-not-a-real-key-0000000000000000",
            LlmProvider = "noop",
        });

        var generator = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        generator.Should().BeOfType<SecretRedactingEmbeddingGeneratorDecorator>();
    }

    [Fact]
    public void OllamaEmbeddingProvider_IsNotWrappedWithSecretRedaction()
    {
        using var provider = BuildProvider(new Context.Providers.ContextProvidersOptions
        {
            EmbeddingProvider = "ollama",
            LlmProvider = "noop",
        });

        var generator = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        generator.Should().NotBeOfType<SecretRedactingEmbeddingGeneratorDecorator>();
        generator.Should().BeOfType<OllamaEmbeddingGeneratorDecorator>();
    }

    [Fact]
    public void NoOpEmbeddingProvider_IsNotWrappedWithSecretRedaction()
    {
        using var provider = BuildProvider(new Context.Providers.ContextProvidersOptions
        {
            EmbeddingProvider = "noop",
            LlmProvider = "noop",
        });

        var generator = provider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();

        generator.Should().NotBeOfType<SecretRedactingEmbeddingGeneratorDecorator>();
    }

    [Fact]
    public void AnthropicLlmProvider_IsWrappedWithSecretRedaction()
    {
        using var provider = BuildProvider(new Context.Providers.ContextProvidersOptions
        {
            EmbeddingProvider = "noop",
            LlmProvider = "anthropic",
            AnthropicApiKey = "sk-ant-test-not-a-real-key-0000000000000",
        });

        var chatClient = provider.GetRequiredService<IChatClient>();

        chatClient.Should().BeOfType<SecretRedactingChatClientDecorator>();
    }

    [Fact]
    public void OllamaLlmProvider_IsNotWrappedWithSecretRedaction()
    {
        using var provider = BuildProvider(new Context.Providers.ContextProvidersOptions
        {
            EmbeddingProvider = "noop",
            LlmProvider = "ollama",
        });

        var chatClient = provider.GetRequiredService<IChatClient>();

        chatClient.Should().NotBeOfType<SecretRedactingChatClientDecorator>();
        chatClient.Should().BeOfType<OllamaChatClientDecorator>();
    }

    [Fact]
    public void NoOpLlmProvider_IsNotWrappedWithSecretRedaction()
    {
        using var provider = BuildProvider(new Context.Providers.ContextProvidersOptions
        {
            EmbeddingProvider = "noop",
            LlmProvider = "noop",
        });

        var chatClient = provider.GetRequiredService<IChatClient>();

        chatClient.Should().NotBeOfType<SecretRedactingChatClientDecorator>();
    }
}
