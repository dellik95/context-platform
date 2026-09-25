using Context.Application.Indexing;
using FluentAssertions;

namespace Context.Application.Tests;

/// <summary>
/// Pure unit coverage for <see cref="ReindexCancellationRegistry"/> -- the per-project
/// <see cref="CancellationTokenSource"/> tracking behind "stop an in-progress reindex"
/// (<c>POST /api/projects/{id}/index/cancel</c> in <c>Context.Host.Api.ProjectEndpoints</c>).
/// </summary>
public sealed class ReindexCancellationRegistryTests
{
    [Fact]
    public void Cancel_returns_false_when_nothing_is_tracked_for_the_project()
    {
        var registry = new ReindexCancellationRegistry();

        registry.Cancel(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void Cancel_cancels_the_token_Begin_returned_and_returns_true()
    {
        var registry = new ReindexCancellationRegistry();
        var projectId = Guid.NewGuid();

        var token = registry.Begin(projectId);
        token.IsCancellationRequested.Should().BeFalse();

        registry.Cancel(projectId).Should().BeTrue();

        token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void TryBegin_leaves_a_running_pass_alone_and_succeeds_again_once_it_ends()
    {
        var registry = new ReindexCancellationRegistry();
        var projectId = Guid.NewGuid();
        var running = registry.Begin(projectId);

        registry.TryBegin(projectId, out _).Should().BeFalse();
        running.IsCancellationRequested.Should().BeFalse();

        registry.End(projectId, running);

        registry.TryBegin(projectId, out var next).Should().BeTrue();
        registry.Cancel(projectId).Should().BeTrue();
        next.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void IsRunning_reflects_Begin_and_End()
    {
        var registry = new ReindexCancellationRegistry();
        var projectId = Guid.NewGuid();

        registry.IsRunning(projectId).Should().BeFalse();

        var token = registry.Begin(projectId);
        registry.IsRunning(projectId).Should().BeTrue();

        registry.End(projectId, token);
        registry.IsRunning(projectId).Should().BeFalse();
    }

    [Fact]
    public void Begin_called_again_for_the_same_project_cancels_the_previous_token_and_replaces_it()
    {
        var registry = new ReindexCancellationRegistry();
        var projectId = Guid.NewGuid();

        var firstToken = registry.Begin(projectId);
        var secondToken = registry.Begin(projectId);

        // The stale/leaked first pass is cancelled out from under it rather than left to leak...
        firstToken.IsCancellationRequested.Should().BeTrue();
        // ...but the newly-started pass is unaffected.
        secondToken.IsCancellationRequested.Should().BeFalse();

        registry.IsRunning(projectId).Should().BeTrue();
    }

    [Fact]
    public void End_with_a_stale_token_does_not_remove_a_newer_Begin_s_entry()
    {
        var registry = new ReindexCancellationRegistry();
        var projectId = Guid.NewGuid();

        var firstToken = registry.Begin(projectId);
        var secondToken = registry.Begin(projectId); // replaces the first entry (see above)

        // A late finally-block End() call from the FIRST (already-superseded, already-cancelled)
        // pass must not tear down the SECOND pass's still-tracked entry.
        registry.End(projectId, firstToken);

        registry.IsRunning(projectId).Should().BeTrue();
        registry.Cancel(projectId).Should().BeTrue();
        secondToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void End_removes_the_tracked_entry_so_a_later_Cancel_is_a_no_op()
    {
        var registry = new ReindexCancellationRegistry();
        var projectId = Guid.NewGuid();

        var token = registry.Begin(projectId);
        registry.End(projectId, token);

        registry.Cancel(projectId).Should().BeFalse();
    }
}
