using Context.Application.Jobs;
using Context.Domain.Enums;
using FluentAssertions;

namespace Context.Application.Tests;

public sealed class JobRetryPolicyTests
{
    [Theory]
    [InlineData(JobType.ProcessHook, 6)]
    [InlineData(JobType.ForgetMemory, 6)]
    [InlineData(JobType.RememberMemory, 10)]
    [InlineData(JobType.EmbedChunk, 5)]
    [InlineData(JobType.IndexFile, 3)]
    [InlineData(JobType.ExtractMemory, 3)]
    public void For_gives_each_job_type_its_attempt_budget(JobType type, int maxAttempts) =>
        JobRetryPolicy.For(type).MaxAttempts.Should().Be(maxAttempts);

    [Fact]
    public void Backoff_doubles_per_attempt_and_stops_at_the_type_cap()
    {
        var hook = JobRetryPolicy.For(JobType.ProcessHook);
        hook.BackoffAfter(1).Should().Be(TimeSpan.FromSeconds(4));
        hook.BackoffAfter(2).Should().Be(TimeSpan.FromSeconds(8));
        hook.BackoffAfter(20).Should().Be(TimeSpan.FromMinutes(5));

        JobRetryPolicy.For(JobType.RememberMemory).BackoffAfter(20).Should().Be(TimeSpan.FromMinutes(15));
    }

    [Theory]
    [InlineData(JobType.ProcessHook, true)]
    [InlineData(JobType.RememberMemory, true)]
    [InlineData(JobType.ForgetMemory, true)]
    [InlineData(JobType.ExtractMemory, true)]
    [InlineData(JobType.SummarizeSession, true)]
    [InlineData(JobType.ReconsolidateMemory, true)]
    [InlineData(JobType.EmbedChunk, false)]
    [InlineData(JobType.IndexFile, false)]
    [InlineData(JobType.UpdateGraph, false)]
    public void IsUserData_marks_the_types_whose_pending_rows_exist_nowhere_else(JobType type, bool expected) =>
        JobRetryPolicy.IsUserData(type).Should().Be(expected);

    [Fact]
    public void UserData_lists_exactly_the_protected_types() =>
        JobRetryPolicy.UserData.Should().OnlyContain(type => JobRetryPolicy.IsUserData(type));
}
