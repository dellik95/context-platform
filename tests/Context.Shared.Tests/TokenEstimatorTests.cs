using FluentAssertions;

namespace Context.Shared.Tests;

public class TokenEstimatorTests
{
    [Fact]
    public void Estimate_EmptyString_ReturnsFloorOfOne()
    {
        TokenEstimator.Estimate(string.Empty).Should().Be(1);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("abc")]
    [InlineData("abcd")]
    public void Estimate_ShortText_IsFlooredAtOne(string text)
    {
        // text.Length / 4 is 0 for anything shorter than 4 chars, but the estimator
        // floors every result at a minimum of 1 token (see TokenEstimator.Estimate).
        TokenEstimator.Estimate(text).Should().Be(1);
    }

    [Fact]
    public void Estimate_NeverReturnsLessThanOne()
    {
        for (var length = 0; length < 20; length++)
        {
            TokenEstimator.Estimate(new string('x', length)).Should().BeGreaterOrEqualTo(1);
        }
    }

    [Fact]
    public void Estimate_LongerText_ProducesLargerOrEqualEstimateThanShorterText()
    {
        var shortText = new string('x', 40);
        var longText = new string('x', 400);

        TokenEstimator.Estimate(longText).Should().BeGreaterOrEqualTo(TokenEstimator.Estimate(shortText));
    }

    [Fact]
    public void Estimate_UsesApproximatelyFourCharsPerToken_ForTextLongEnoughToClearTheFloor()
    {
        var text = new string('x', 400);

        TokenEstimator.Estimate(text).Should().Be(100);
    }

    [Fact]
    public void Estimate_IsMonotonicNonDecreasing_AsTextGrows()
    {
        var previous = TokenEstimator.Estimate(string.Empty);

        for (var length = 1; length <= 100; length++)
        {
            var current = TokenEstimator.Estimate(new string('x', length));
            current.Should().BeGreaterOrEqualTo(previous);
            previous = current;
        }
    }
}
