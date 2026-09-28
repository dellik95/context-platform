using Context.Memory.Consolidation;
using FluentAssertions;

namespace Context.Memory.Tests;

public sealed class TopicNormalizerTests
{
    [Theory]
    [InlineData("Payments webhooks", "payment-webhook")]
    [InlineData("Checkout page flow", "checkout")]
    [InlineData("checkout pages", "checkout")]
    [InlineData("The retry policies", "retry-policy")]
    [InlineData("Auth/OAuth: token refresh!", "auth-oauth-token-refresh")]
    [InlineData("User's sessions", "user-session")]
    [InlineData("Cache caches", "cache")]
    [InlineData("Build classes", "build-class")]
    [InlineData("Status analysis bus", "status-analysis-bus")]
    public void Normalize_reduces_a_topic_to_its_grouping_key(string topic, string expected)
    {
        TopicNormalizer.Normalize(topic).Should().Be(expected);
    }

    [Fact]
    public void Normalize_keeps_filler_words_when_nothing_else_is_left()
    {
        TopicNormalizer.Normalize("Page flow").Should().Be("page-flow");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! ---")]
    public void Normalize_returns_null_for_a_blank_topic(string? topic)
    {
        TopicNormalizer.Normalize(topic).Should().BeNull();
    }

    [Fact]
    public void Normalize_caps_the_key_length_without_a_trailing_separator()
    {
        var topic = string.Join(' ', Enumerable.Range(1, 60).Select(i => $"word{i}"));

        var key = TopicNormalizer.Normalize(topic)!;

        key.Length.Should().BeLessThanOrEqualTo(TopicNormalizer.MaxKeyLength);
        key.Should().NotEndWith("-");
    }
}
