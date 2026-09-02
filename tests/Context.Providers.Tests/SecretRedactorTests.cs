using Context.Providers.Security;
using FluentAssertions;

namespace Context.Providers.Tests;

public class SecretRedactorTests
{
    [Theory]
    [InlineData("sk-abcdefghijklmnopqrstuvwxyz1234567890")]
    [InlineData("Bearer sk-abcdefghijklmnopqrstuvwxyz1234567890 in the header")]
    [InlineData("ghp_1234567890ABCDEFGHIJabcdefghijklmnop")]
    [InlineData("gho_1234567890ABCDEFGHIJabcdefghijklmnop")]
    [InlineData("ghu_1234567890ABCDEFGHIJabcdefghijklmnop")]
    [InlineData("aws_access_key_id = AKIAIOSFODNN7EXAMPLE")]
    [InlineData("apiKey = \"aB3xR9mZ7pQ2vL5nK8jH1wT4yU6cD0sFgE\"")]
    [InlineData("token: \"9f8e7d6c5b4a3f2e1d0c9b8a7f6e5d4c3b2a1f0\"")]
    [InlineData("SECRET_KEY='Zx7qP2mR9vL4nK8jH1wT4yU6cD0sFgEaB3xR9m'")]
    public void Redact_MatchesKnownSecretShapes(string input)
    {
        var result = SecretRedactor.Redact(input, out var count);

        count.Should().BeGreaterThan(0);
        result.Should().Contain(SecretRedactor.RedactedPlaceholder);
        result.Should().NotBe(input);
    }

    [Theory]
    [InlineData("apiKey = \"test\"")]
    [InlineData("apiKey = \"12345\"")]
    [InlineData("password = \"changeme_please_1234567890\"")]
    [InlineData("secret = \"your-secret-goes-here-abcdefgh\"")]
    [InlineData("token = \"PLACEHOLDER_VALUE_1234567890\"")]
    [InlineData("// remember to rotate the api key and token periodically")]
    [InlineData("githubUrl = \"https://github.com/foo/ghost_module\"")]
    [InlineData("var apiKeyLabel = \"API Key:\";")]
    [InlineData("public string Password { get; set; }")]
    [InlineData("the quick brown fox jumps over the lazy dog")]
    public void Redact_DoesNotFlagNormalCodeOrPlaceholders(string input)
    {
        var result = SecretRedactor.Redact(input, out var count);

        count.Should().Be(0);
        result.Should().Be(input);
    }

    [Fact]
    public void Redact_OnGenericMatch_OnlyRedactsTheValueSpan_NotTheWholeLine()
    {
        const string input = "apiKey = \"aB3xR9mZ7pQ2vL5nK8jH1wT4yU6cD0sFgE\";";

        var result = SecretRedactor.Redact(input, out var count);

        count.Should().Be(1);
        result.Should().StartWith("apiKey = \"");
        result.Should().EndWith("\";");
        result.Should().Contain(SecretRedactor.RedactedPlaceholder);
        result.Should().NotContain("aB3xR9mZ7pQ2vL5nK8jH1wT4yU6cD0sFgE");
    }

    [Fact]
    public void Redact_OnProviderPrefixedMatch_RedactsOnlyTheTokenSpan_NotSurroundingText()
    {
        const string input = "Authorization header uses sk-abcdefghijklmnopqrstuvwxyz1234567890 for auth.";

        var result = SecretRedactor.Redact(input, out var count);

        count.Should().Be(1);
        result.Should().StartWith("Authorization header uses ");
        result.Should().EndWith(" for auth.");
    }

    [Fact]
    public void Redact_WithMultipleSecrets_RedactsEachOne()
    {
        const string input = "openai: sk-abcdefghijklmnopqrstuvwxyz1234567890\n" +
                              "github: ghp_1234567890ABCDEFGHIJabcdefghijklmnop\n";

        var result = SecretRedactor.Redact(input, out var count);

        count.Should().Be(2);
    }

    [Fact]
    public void Redact_NullOrEmpty_ReturnsUnchanged()
    {
        SecretRedactor.Redact(string.Empty, out var count).Should().Be(string.Empty);
        count.Should().Be(0);
    }
}
