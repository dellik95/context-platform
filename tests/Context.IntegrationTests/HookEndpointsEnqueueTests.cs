using System.Text.Json;
using Context.Application;
using Context.Application.Jobs;
using Context.Claude.Hooks;
using Context.Dashboard.Server.Hooks;
using Context.Dashboard.Server.Security;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Retrieval.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Moq;

namespace Context.IntegrationTests;

/// <summary>
/// A hook's request path does one thing that can lose data: insert a <see cref="JobType.ProcessHook"/> job.
/// These tests drive each endpoint handler directly with a strict <see cref="IContextEngine"/> (only the
/// read-only context building may touch it) and check what was queued.
/// </summary>
public sealed class HookEndpointsEnqueueTests
{
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly string _claudeSessionId = $"claude-session-{Guid.NewGuid():N}";
    private readonly Mock<IJobQueue> _jobQueue = new(MockBehavior.Strict);
    private readonly Mock<IContextEngine> _engine = new(MockBehavior.Strict);
    private readonly CurrentProjectAccessor _project;
    private readonly List<ProcessHookPayload> _queued = [];

    public HookEndpointsEnqueueTests()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items[CurrentProjectAccessor.HttpContextItemKey] =
            new Project { Id = _projectId, Name = "Hook Project", RootPath = "C:/hook-project", CreatedAt = DateTimeOffset.UtcNow };
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns(httpContext);
        _project = new CurrentProjectAccessor(accessor.Object);

        _jobQueue
            .Setup(q => q.EnqueueAsync(JobType.ProcessHook, It.IsAny<object>(), _projectId, JobPriority.Hook, It.IsAny<CancellationToken>()))
            .Callback<JobType, object, Guid?, int, CancellationToken>((_, payload, _, _, _) => _queued.Add((ProcessHookPayload)payload))
            .ReturnsAsync(Guid.NewGuid());
    }

    private ProcessHookPayload Queued => _queued.Should().ContainSingle().Subject;

    private static JsonElement Input(ProcessHookPayload payload) => JsonDocument.Parse(payload.InputJson).RootElement;

    [Fact]
    public async Task PostToolUseAsync_queues_the_raw_hook_and_touches_nothing_else()
    {
        var before = DateTimeOffset.UtcNow;
        var input = new PostToolUseInput(_claudeSessionId, "Read", """{"file_path":"src/A.cs"}""", """{"ok":true}""");

        var result = await HookEndpoints.PostToolUseAsync(input, _jobQueue.Object, _project, CancellationToken.None);

        result.Should().BeOfType<Ok<HookOutput>>().Which.Value!.AdditionalContext.Should().BeNull();
        var payload = Queued;
        payload.Type.Should().Be(HookType.PostToolUse);
        payload.ReceivedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow);
        var json = Input(payload);
        json.GetProperty("session_id").GetString().Should().Be(_claudeSessionId);
        json.GetProperty("tool_name").GetString().Should().Be("Read");
        json.GetProperty("tool_input").GetProperty("file_path").GetString().Should().Be("src/A.cs");
        json.GetProperty("tool_response").GetProperty("ok").GetBoolean().Should().BeTrue();
        _engine.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task SessionEndAsync_and_StopAsync_queue_their_hook_and_return_no_context()
    {
        var end = await HookEndpoints.SessionEndAsync(new SessionEndInput(_claudeSessionId, "logout"), _jobQueue.Object, _project, CancellationToken.None);
        var stop = await HookEndpoints.StopAsync(new StopInput(_claudeSessionId, "Switched to Polly."), _jobQueue.Object, _project, CancellationToken.None);

        end.Should().BeOfType<Ok<HookOutput>>().Which.Value!.AdditionalContext.Should().BeNull();
        stop.Should().BeOfType<Ok<HookOutput>>().Which.Value!.AdditionalContext.Should().BeNull("a Stop hook must never return context");
        _queued.Select(p => p.Type).Should().Equal(HookType.SessionEnd, HookType.Stop);
        Input(_queued[0]).GetProperty("reason").GetString().Should().Be("logout");
        Input(_queued[1]).GetProperty("last_assistant_message").GetString().Should().Be("Switched to Polly.");
    }

    [Fact]
    public async Task UserPromptSubmitAsync_queues_the_prompt_and_keys_the_delivery_cache_by_the_claude_session()
    {
        var pack = new ContextPack("arch", [new SearchHit(Guid.NewGuid(), "Symbol", "Retry.Policy", 1.0, "snippet")], [], [], 10, false, 0, "sig");
        _engine
            .Setup(e => e.SearchContextAsync(It.Is<SearchQuery>(q => q.ProjectId == _projectId && q.Text == "How does retry work?" && q.SessionKey == _claudeSessionId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(pack);

        var result = await HookEndpoints.UserPromptSubmitAsync(
            new UserPromptSubmitInput(_claudeSessionId, "How does retry work?"), _engine.Object, _jobQueue.Object, _project, CancellationToken.None);

        result.Should().BeOfType<Ok<HookOutput>>().Which.Value!.AdditionalContext.Should().Contain("Retry.Policy");
        Queued.Type.Should().Be(HookType.UserPromptSubmit);
        Input(Queued).GetProperty("prompt").GetString().Should().Be("How does retry work?");
    }

    [Fact]
    public async Task UserPromptSubmitAsync_still_returns_and_has_queued_the_prompt_when_context_building_fails()
    {
        _engine.Setup(e => e.SearchContextAsync(It.IsAny<SearchQuery>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("embedding provider down"));

        var result = await HookEndpoints.UserPromptSubmitAsync(
            new UserPromptSubmitInput(_claudeSessionId, "anything"), _engine.Object, _jobQueue.Object, _project, CancellationToken.None);

        result.Should().BeOfType<Ok<HookOutput>>().Which.Value!.AdditionalContext.Should().BeNull();
        Queued.Type.Should().Be(HookType.UserPromptSubmit);
    }

    [Fact]
    public async Task SessionStartAsync_queues_the_hook_and_builds_context_from_memory()
    {
        _engine.Setup(e => e.SearchMemoryAsync(_projectId, string.Empty, false, It.IsAny<CancellationToken>(), null)).ReturnsAsync([]);

        var result = await HookEndpoints.SessionStartAsync(
            new SessionStartInput(_claudeSessionId, "startup", "/repo"), _engine.Object, _jobQueue.Object, _project, CancellationToken.None);

        result.Should().BeOfType<Ok<HookOutput>>().Which.Value!.AdditionalContext.Should().BeNull("there are no memories yet");
        Queued.Type.Should().Be(HookType.SessionStart);
        Input(Queued).GetProperty("cwd").GetString().Should().Be("/repo");
    }

    [Fact]
    public async Task PostCompactAsync_queues_the_stdin_summary()
    {
        await HookEndpoints.PostCompactAsync(
            new PostCompactInput(_claudeSessionId, "auto", CompactSummary: "Chose Polly over hand-rolled retries."), _jobQueue.Object, _project, CancellationToken.None);

        Queued.Type.Should().Be(HookType.PostCompact);
        Input(Queued).GetProperty("compact_summary").GetString().Should().Be("Chose Polly over hand-rolled retries.");
    }

    [Fact]
    public async Task PostCompactAsync_resolves_the_summary_from_the_transcript_when_stdin_has_none()
    {
        var transcript = Path.Combine(Path.GetTempPath(), $"transcript-{Guid.NewGuid():N}.jsonl");
        await System.IO.File.WriteAllLinesAsync(transcript,
        [
            """{"type":"system","subtype":"compact_boundary"}""",
            """{"type":"user","isCompactSummary":true,"message":{"content":"Moved retries into Polly."}}""",
        ]);

        try
        {
            await HookEndpoints.PostCompactAsync(
                new PostCompactInput(_claudeSessionId, "manual", TranscriptPath: transcript), _jobQueue.Object, _project, CancellationToken.None);
        }
        finally
        {
            System.IO.File.Delete(transcript);
        }

        Input(Queued).GetProperty("compact_summary").GetString().Should().Be("Moved retries into Polly.",
            "the transcript tail moves on, so the job gets the text resolved at request time");
    }

    [Fact]
    public async Task A_failed_enqueue_surfaces_so_the_hook_is_not_acknowledged()
    {
        var failing = new Mock<IJobQueue>();
        failing.Setup(q => q.EnqueueAsync(It.IsAny<JobType>(), It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database is locked"));

        var act = () => HookEndpoints.PostToolUseAsync(
            new PostToolUseInput(_claudeSessionId, "Read", "{}", "{}"), failing.Object, _project, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("Thanks!")]
    [InlineData("looks good.")]
    [InlineData("/compact")]
    public async Task UserPromptSubmitAsync_skips_context_retrieval_for_trivial_prompts_while_still_queueing(string prompt)
    {
        var strictEngine = new Mock<IContextEngine>(MockBehavior.Strict);

        var result = await HookEndpoints.UserPromptSubmitAsync(
            new UserPromptSubmitInput(_claudeSessionId, prompt), strictEngine.Object, _jobQueue.Object, _project, CancellationToken.None);

        result.Should().BeOfType<Ok<HookOutput>>().Which.Value!.AdditionalContext.Should().BeNull();
        Queued.Type.Should().Be(HookType.UserPromptSubmit);
        strictEngine.VerifyNoOtherCalls();
    }
}
