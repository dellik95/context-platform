using Context.Providers.Embeddings;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Context.Providers.Tests;

public class EmbeddingBatchingTests
{
    [Theory]
    [InlineData("ollama", 16, 32_000)]
    [InlineData(" Ollama ", 16, 32_000)]
    [InlineData("openai", 100, 250_000)]
    [InlineData("foundry", 1, int.MaxValue)]
    [InlineData("noop", EmbeddingBatchLimits.MaxConfigurableInputs, int.MaxValue)]
    public void ForProvider_returns_the_provider_default(string provider, int maxInputs, int maxChars)
    {
        EmbeddingBatchLimits.ForProvider(provider).Should().Be(new EmbeddingBatchLimits(maxInputs, maxChars));
    }

    [Theory]
    [InlineData(0, 16)]
    [InlineData(1, 1)]
    [InlineData(64, 64)]
    public void WithMaxInputs_overrides_the_input_count_but_keeps_the_char_cap(int batchSize, int expectedMaxInputs)
    {
        EmbeddingBatchLimits.ForProvider("ollama").WithMaxInputs(batchSize).Should().Be(new EmbeddingBatchLimits(expectedMaxInputs, 32_000));
    }

    [Fact]
    public void Split_caps_each_batch_by_count_and_by_total_chars_and_sends_an_oversized_text_alone()
    {
        var limits = new EmbeddingBatchLimits(3, 10);

        var batches = limits.Split(["aa", "bb", "cc", "dd", "eeeeeeee", "ffffffffffffffff", "g"], t => t).ToList();

        batches.Should().BeEquivalentTo(
            new[] { new[] { "aa", "bb", "cc" }, ["dd", "eeeeeeee"], ["ffffffffffffffff"], ["g"] },
            o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task BatchingGenerator_splits_a_call_into_requests_within_its_limits_and_keeps_input_order()
    {
        var inner = new RecordingGenerator();
        using var sut = new BatchingEmbeddingGenerator(inner, new EmbeddingBatchLimits(2, int.MaxValue));

        var result = await sut.GenerateAsync(["a", "bb", "ccc", "dddd", "eeeee"]);

        inner.Requests.Should().BeEquivalentTo(new[] { new[] { "a", "bb" }, ["ccc", "dddd"], ["eeeee"] }, o => o.WithStrictOrdering());
        result.Select(e => e.Vector.Span[0]).Should().Equal(1, 2, 3, 4, 5);
        result.Usage!.InputTokenCount.Should().Be(15);
    }

    [Fact]
    public void BatchingGenerator_exposes_its_limits_and_the_inner_metadata()
    {
        var limits = new EmbeddingBatchLimits(8, 1000);
        using var sut = new BatchingEmbeddingGenerator(new RecordingGenerator(), limits);

        sut.GetBatchLimits().Should().Be(limits);
        sut.GetProviderModelKey().Should().Be("fake:fake-model");
        new RecordingGenerator().GetBatchLimits().Should().Be(EmbeddingBatchLimits.None);
    }

    [Fact]
    public async Task BatchingGenerator_rejects_a_response_with_fewer_embeddings_than_inputs()
    {
        using var sut = new BatchingEmbeddingGenerator(new RecordingGenerator { DropLast = true }, new EmbeddingBatchLimits(4, int.MaxValue));

        var act = () => sut.GenerateAsync(["a", "b"]);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*1 embeddings for 2 inputs*");
    }

    [Fact]
    public async Task BatchingGenerator_turns_a_provider_context_length_error_into_EmbeddingContextLengthException()
    {
        var inner = new RecordingGenerator
        {
            Failure = new InvalidOperationException("This model's maximum context length is 8192 tokens, however you requested 9000 tokens"),
        };
        using var sut = new BatchingEmbeddingGenerator(inner, new EmbeddingBatchLimits(4, int.MaxValue));

        var act = () => sut.GenerateAsync(["a"]);

        (await act.Should().ThrowAsync<EmbeddingContextLengthException>()).WithInnerException<InvalidOperationException>();
    }

    [Fact]
    public async Task BatchingGenerator_passes_other_failures_through_unchanged()
    {
        var failure = new HttpRequestException("connection refused");
        using var sut = new BatchingEmbeddingGenerator(new RecordingGenerator { Failure = failure }, new EmbeddingBatchLimits(4, int.MaxValue));

        var act = () => sut.GenerateAsync(["a"]);

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.Should().BeSameAs(failure);
    }

    /// <summary>Returns a one-component vector holding each input's length.</summary>
    private sealed class RecordingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly EmbeddingGeneratorMetadata _metadata = new("fake", null, "fake-model");

        public List<string[]> Requests { get; } = [];

        public bool DropLast { get; init; }

        public Exception? Failure { get; init; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            var inputs = values.ToArray();
            Requests.Add(inputs);
            if (Failure is not null)
            {
                throw Failure;
            }

            var embeddings = inputs.Take(DropLast ? inputs.Length - 1 : inputs.Length).Select(t => new Embedding<float>(new float[] { t.Length }));
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings)
            {
                Usage = new UsageDetails { InputTokenCount = inputs.Sum(t => t.Length) },
            });
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(EmbeddingGeneratorMetadata) ? _metadata : null;

        public void Dispose()
        {
        }
    }
}
