using FluentAssertions;

namespace Context.Retrieval.Tests;

public sealed class QueryIntentTests
{
    [Theory]
    [InlineData("what's left to do", true)]
    [InlineData("open issues in the indexer", true)]
    [InlineData("what changed recently", false)]
    public void IsPendingWork_detects_pending_work_wording(string query, bool expected) =>
        QueryIntent.IsPendingWork(query).Should().Be(expected);
}
