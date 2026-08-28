using System.Text;
using FluentAssertions;

namespace Context.Shared.Tests;

public class HashingTests
{
    // Well-known SHA-256 hash of the empty input (lowercase hex).
    private const string EmptySha256Hex = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void HashText_SameInput_ProducesSameHash()
    {
        var first = Hashing.HashText("the quick brown fox");
        var second = Hashing.HashText("the quick brown fox");

        first.Should().Be(second);
    }

    [Fact]
    public void HashText_DifferentInput_ProducesDifferentHash()
    {
        var first = Hashing.HashText("the quick brown fox");
        var second = Hashing.HashText("the quick brown fog");

        first.Should().NotBe(second);
    }

    [Fact]
    public void HashBytes_SameInput_ProducesSameHash()
    {
        var bytes = Encoding.UTF8.GetBytes("some content to hash");

        var first = Hashing.HashBytes(bytes);
        var second = Hashing.HashBytes(bytes);

        first.Should().Be(second);
    }

    [Fact]
    public void HashBytes_DifferentInput_ProducesDifferentHash()
    {
        var first = Hashing.HashBytes(Encoding.UTF8.GetBytes("payload-a"));
        var second = Hashing.HashBytes(Encoding.UTF8.GetBytes("payload-b"));

        first.Should().NotBe(second);
    }

    [Fact]
    public void HashText_EmptyString_DoesNotThrowAndMatchesWellKnownSha256OfEmpty()
    {
        var act = () => Hashing.HashText(string.Empty);

        act.Should().NotThrow();
        Hashing.HashText(string.Empty).Should().Be(EmptySha256Hex);
    }

    [Fact]
    public void HashBytes_EmptyArray_DoesNotThrowAndMatchesWellKnownSha256OfEmpty()
    {
        var act = () => Hashing.HashBytes([]);

        act.Should().NotThrow();
        Hashing.HashBytes([]).Should().Be(EmptySha256Hex);
    }

    [Fact]
    public void HashText_ProducesLowercaseHexString()
    {
        var hash = Hashing.HashText("Some Mixed Case Content 123!@#");

        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void HashText_AndHashBytes_AgreeForUtf8EncodingOfSameString()
    {
        const string text = "café — mixed ASCII and non-ASCII 你好";

        var fromText = Hashing.HashText(text);
        var fromBytes = Hashing.HashBytes(Encoding.UTF8.GetBytes(text));

        fromText.Should().Be(fromBytes);
    }
}
