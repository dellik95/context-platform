using FluentAssertions;

namespace Context.Retrieval.Tests;

public sealed class QueryIntentTests
{
    [Theory]
    [InlineData("What changed in the auth flow?")]
    [InlineData("when did we switch to SQLite")]
    [InlineData("history of the retry policy")]
    [InlineData("timeline for the migration")]
    [InlineData("last time we touched the indexer")]
    [InlineData("what did we do recently")]
    [InlineData("CHANGELOG for the hub")]
    public void IsChangeHistory_detects_change_history_wording(string query) =>
        QueryIntent.IsChangeHistory(query).Should().BeTrue();

    [Theory]
    [InlineData("how does token refresh work")]
    [InlineData("where is the retry policy configured")]
    [InlineData("")]
    public void IsChangeHistory_is_false_for_domain_questions(string query) =>
        QueryIntent.IsChangeHistory(query).Should().BeFalse();

    [Theory]
    [InlineData("what's left to do", true)]
    [InlineData("open issues in the indexer", true)]
    [InlineData("what changed recently", false)]
    public void IsPendingWork_detects_pending_work_wording(string query, bool expected) =>
        QueryIntent.IsPendingWork(query).Should().Be(expected);
}
