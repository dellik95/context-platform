using System.Text.Json;
using Context.Application.Abstractions;
using Context.Application.Jobs;
using Context.Application.Jobs.Handlers;
using Context.Application.Notifications;
using Context.Application.Settings;
using Context.Claude.Hooks;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Graph.Abstractions;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Context.Infrastructure.Repositories;
using Context.Memory.Abstractions;
using Context.Providers.Abstractions;
using Context.Retrieval;
using Context.Retrieval.Abstractions;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Context.Application.Tests;

/// <summary>
/// <see cref="ProcessHookJobHandler"/> against a real SQLite database with the real engine, repositories
/// and unit of work: the properties that matter (one transaction per hook, a retry changes nothing, parallel
/// jobs never fork a session) only show up with real transactions.
/// </summary>
public sealed class ProcessHookJobHandlerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-hook-tests-{Guid.NewGuid():N}.db");
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly string _claudeSessionId = $"claude-{Guid.NewGuid():N}";
    private readonly Mock<IJobQueue> _jobQueue = new();
    private readonly Mock<IHookInvocationNotifier> _notifier = new();
    private readonly Mock<IProjectSettingsService> _settings = new();
    private int _minEventsForExtraction = 100;

    public ProcessHookJobHandlerTests()
    {
        using var db = CreateContext();
        db.Database.Migrate();
        db.Projects.Add(new Project { Id = _projectId, Name = "Hooks", RootPath = RootPath, CreatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();

        _settings.Setup(s => s.GetAsync(_projectId, It.IsAny<CancellationToken>())).ReturnsAsync(() => new ProjectSettingsDto(
            EmbeddingProvider: "noop", LlmProvider: "noop", OllamaBaseUrl: null, OllamaEmbeddingModel: null, OllamaChatModel: null,
            ReindexEnabled: false, ReindexIntervalMinutes: 60, LastReindexScheduledAt: null,
            MinEventsForMemoryExtraction: _minEventsForExtraction));
    }

    private static string RootPath => OperatingSystem.IsWindows() ? @"C:\hook-project" : "/hook-project";

    private ContextDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<ContextDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    private ProcessHookJobHandler CreateHandler(ContextDbContext db, IProjectSettingsService? settings = null)
    {
        var sessions = new SessionRepository(db);
        var jobs = new JobRepository(db);
        var unitOfWork = new UnitOfWork(db);
        var engine = new ContextEngine(
            new ContextPackBuilder(new Mock<ISearchEngine>().Object), _jobQueue.Object, new Mock<IGraphTraversal>().Object,
            new Mock<IGraphCacheProvider>().Object, new ProjectRepository(db), new FileRepository(db), new SymbolRepository(db),
            new ChunkRepository(db), new MemoryRepository(db), sessions, new SessionDeliveryCache(), settings ?? _settings.Object,
            new Mock<IMemoryConsolidator>().Object, new Mock<IMemoryStore>().Object, new Mock<ISessionSummaryVectorIndex>().Object,
            new Mock<IProviderResolver>().Object, unitOfWork, jobs);
        return new ProcessHookJobHandler(
            engine, unitOfWork, jobs, new HookInvocationRepository(db), new ProjectRepository(db), _notifier.Object);
    }

    private Job MakeJob(HookType type, object input, DateTimeOffset? receivedAt = null, DateTimeOffset? createdAt = null) => new()
    {
        Id = Guid.NewGuid(),
        Type = JobType.ProcessHook,
        ProjectId = _projectId,
        PayloadJson = JsonSerializer.Serialize(new ProcessHookPayload(type, JsonSerializer.Serialize(input, input.GetType()), receivedAt ?? DateTimeOffset.UtcNow)),
        Status = JobStatus.Running,
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
    };

    private async Task RunAsync(Job job, IProjectSettingsService? settings = null)
    {
        await using var db = CreateContext();
        await CreateHandler(db, settings).HandleAsync(job, CancellationToken.None);
    }

    private PostToolUseInput ToolUse(string tool, string toolInput = "{}") => new(_claudeSessionId, tool, toolInput, "{}");

    private async Task<T> QueryAsync<T>(Func<ContextDbContext, Task<T>> query)
    {
        await using var db = CreateContext();
        return await query(db);
    }

    [Fact]
    public async Task PostToolUse_creates_the_session_and_records_a_classified_event_with_the_jobs_id_and_receive_time()
    {
        var receivedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        var editedFile = Path.Combine(RootPath, "src", "Context.Memory", "X.cs");
        var job = MakeJob(HookType.PostToolUse, ToolUse("Edit", JsonSerializer.Serialize(new { file_path = editedFile })), receivedAt);

        await RunAsync(job);

        var session = await QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync(s => s.ClaudeSessionId == _claudeSessionId));
        session.ProjectId.Should().Be(_projectId);
        var sessionEvent = await QueryAsync(db => db.SessionEvents.AsNoTracking().SingleAsync(e => e.SessionId == session.Id));
        sessionEvent.Id.Should().Be(job.Id);
        sessionEvent.Type.Should().Be(SessionEventType.FileEdit);
        sessionEvent.Timestamp.Should().Be(receivedAt);
        using var payload = JsonDocument.Parse(sessionEvent.PayloadJson);
        payload.RootElement.GetProperty("path").GetString().Should().Be("src/Context.Memory/X.cs");
        payload.RootElement.GetProperty("toolName").GetString().Should().Be("Edit");

        var invocation = await QueryAsync(db => db.HookInvocations.AsNoTracking().SingleAsync());
        invocation.Id.Should().Be(job.Id);
        invocation.Type.Should().Be(HookType.PostToolUse);
        invocation.ReceivedAt.Should().Be(receivedAt);
        _notifier.Verify(n => n.NotifyAsync(_projectId, It.Is<HookInvocationEvent>(e => e.Type == HookType.PostToolUse && e.TotalCount == 1), It.IsAny<CancellationToken>()), Times.Once);
        _jobQueue.Verify(q => q.WakeAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never, "a plain event queues no job, so nothing needs waking");
    }

    [Theory]
    [InlineData("Read", SessionEventType.FileRead)]
    [InlineData("NotebookEdit", SessionEventType.FileEdit)]
    [InlineData("Bash", SessionEventType.Command)]
    [InlineData("WebFetch", SessionEventType.ToolCall)]
    public async Task PostToolUse_classifies_the_tool(string tool, SessionEventType expected)
    {
        await RunAsync(MakeJob(HookType.PostToolUse, ToolUse(tool)));

        (await QueryAsync(db => db.SessionEvents.AsNoTracking().SingleAsync())).Type.Should().Be(expected);
    }

    [Fact]
    public async Task Running_the_same_job_again_changes_nothing()
    {
        var job = MakeJob(HookType.PostToolUse, ToolUse("Read"));

        await RunAsync(job);
        await RunAsync(job);

        (await QueryAsync(db => db.SessionEvents.CountAsync())).Should().Be(1);
        (await QueryAsync(db => db.HookInvocations.CountAsync())).Should().Be(1);
        (await QueryAsync(db => db.Sessions.CountAsync())).Should().Be(1);
        _notifier.Verify(n => n.NotifyAsync(It.IsAny<Guid>(), It.IsAny<HookInvocationEvent>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_failure_part_way_through_rolls_back_the_session_event_and_invocation()
    {
        var failing = new Mock<IProjectSettingsService>();
        failing.Setup(s => s.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("settings file locked"));

        var act = () => RunAsync(MakeJob(HookType.PostToolUse, ToolUse("Read")), failing.Object);

        await act.Should().ThrowAsync<IOException>();
        (await QueryAsync(db => db.Sessions.CountAsync())).Should().Be(0, "the session created earlier in the same transaction is rolled back too");
        (await QueryAsync(db => db.SessionEvents.CountAsync())).Should().Be(0);
        (await QueryAsync(db => db.HookInvocations.CountAsync())).Should().Be(0);
        _notifier.Verify(n => n.NotifyAsync(It.IsAny<Guid>(), It.IsAny<HookInvocationEvent>(), It.IsAny<CancellationToken>()), Times.Never);

        await RunAsync(MakeJob(HookType.PostToolUse, ToolUse("Read")));
        (await QueryAsync(db => db.SessionEvents.CountAsync())).Should().Be(1, "the retry processes the hook normally");
    }

    [Fact]
    public async Task Parallel_SessionStart_jobs_for_one_claude_session_create_exactly_one_session()
    {
        var jobs = Enumerable.Range(0, 6).Select(_ => MakeJob(HookType.SessionStart, new SessionStartInput(_claudeSessionId, "startup", RootPath))).ToList();

        await Task.WhenAll(jobs.Select(job => Task.Run(() => RunAsync(job))));

        (await QueryAsync(db => db.Sessions.CountAsync(s => s.ClaudeSessionId == _claudeSessionId))).Should().Be(1);
        (await QueryAsync(db => db.HookInvocations.CountAsync())).Should().Be(6);
    }

    [Fact]
    public async Task SessionStart_records_the_working_directory_only_when_it_creates_the_session()
    {
        await RunAsync(MakeJob(HookType.SessionStart, new SessionStartInput(_claudeSessionId, "startup", "/first")));
        await RunAsync(MakeJob(HookType.SessionStart, new SessionStartInput(_claudeSessionId, "resume", "/second")));

        (await QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync())).Cwd.Should().Be("/first");
    }

    [Fact]
    public async Task UserPromptSubmit_records_the_prompt_as_an_event()
    {
        await RunAsync(MakeJob(HookType.UserPromptSubmit, new UserPromptSubmitInput(_claudeSessionId, "How does retry work?")));

        var sessionEvent = await QueryAsync(db => db.SessionEvents.AsNoTracking().SingleAsync());
        sessionEvent.Type.Should().Be(SessionEventType.Prompt);
        sessionEvent.PayloadJson.Should().Contain("How does retry work?");
    }

    [Fact]
    public async Task Stop_records_the_assistant_reply_and_skips_a_blank_one()
    {
        await RunAsync(MakeJob(HookType.Stop, new StopInput(_claudeSessionId, "   ")));
        (await QueryAsync(db => db.SessionEvents.CountAsync())).Should().Be(0);
        (await QueryAsync(db => db.HookInvocations.CountAsync())).Should().Be(1, "the invocation is still recorded for the dashboard");

        await RunAsync(MakeJob(HookType.Stop, new StopInput(_claudeSessionId, "Switched the retry policy to Polly.")));

        var sessionEvent = await QueryAsync(db => db.SessionEvents.AsNoTracking().SingleAsync());
        sessionEvent.Type.Should().Be(SessionEventType.AssistantMessage);
        sessionEvent.PayloadJson.Should().Contain("Switched the retry policy to Polly.");
    }

    [Fact]
    public async Task The_event_that_fills_a_batch_commits_with_the_claim_and_its_ExtractMemory_job()
    {
        _minEventsForExtraction = 2;

        await RunAsync(MakeJob(HookType.PostToolUse, ToolUse("Read")));
        (await QueryAsync(db => db.Jobs.CountAsync(j => j.Type == JobType.ExtractMemory))).Should().Be(0);

        await RunAsync(MakeJob(HookType.PostToolUse, ToolUse("Edit")));

        var extract = await QueryAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Type == JobType.ExtractMemory));
        extract.ProjectId.Should().Be(_projectId);
        var batchId = JsonSerializer.Deserialize<ExtractMemoryPayload>(extract.PayloadJson)!.EventBatchId;
        (await QueryAsync(db => db.SessionEvents.CountAsync(e => e.ExtractionBatchId == batchId))).Should().Be(2);
    }

    [Fact]
    public async Task SessionEnd_ends_the_session_flushes_the_batch_and_queues_the_summary()
    {
        await RunAsync(MakeJob(HookType.PostToolUse, ToolUse("Read")));

        await RunAsync(MakeJob(HookType.SessionEnd, new SessionEndInput(_claudeSessionId, "logout")));

        var session = await QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync());
        session.Status.Should().Be(SessionStatus.Ended);
        session.EndReason.Should().Be("logout");
        (await QueryAsync(db => db.Jobs.CountAsync(j => j.Type == JobType.ExtractMemory))).Should().Be(1);
        var summary = await QueryAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Type == JobType.SummarizeSession));
        JsonSerializer.Deserialize<SummarizeSessionPayload>(summary.PayloadJson)!.IsSessionEnd.Should().BeTrue();
        _jobQueue.Verify(q => q.WakeAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()), Times.Never,
            "the jobs were inserted under the hook's open transaction: waking the lanes then would run under the write lock, before the commit");
    }

    [Fact]
    public async Task A_hook_that_created_the_session_first_leaves_its_working_directory_to_be_filled_in_by_SessionStart()
    {
        await RunAsync(MakeJob(HookType.PostToolUse, ToolUse("Read")));
        (await QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync())).Cwd.Should().BeNull();

        await RunAsync(MakeJob(HookType.SessionStart, new SessionStartInput(_claudeSessionId, "startup", "/late-cwd")));

        (await QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync())).Cwd.Should().Be("/late-cwd");
    }

    [Fact]
    public async Task PostCompact_queues_extraction_from_the_summary_and_a_session_summary()
    {
        await RunAsync(MakeJob(HookType.PostCompact, new PostCompactInput(_claudeSessionId, "auto", CompactSummary: "Chose Polly over hand-rolled retries.")));

        var extract = await QueryAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Type == JobType.ExtractMemory));
        JsonSerializer.Deserialize<ExtractMemoryPayload>(extract.PayloadJson)!.CompactSummary.Should().Be("Chose Polly over hand-rolled retries.");
        var summary = await QueryAsync(db => db.Jobs.AsNoTracking().SingleAsync(j => j.Type == JobType.SummarizeSession));
        JsonSerializer.Deserialize<SummarizeSessionPayload>(summary.PayloadJson)!.IsSessionEnd.Should().BeFalse();
    }

    [Fact]
    public async Task PostCompact_without_a_summary_only_requests_the_session_summary()
    {
        await RunAsync(MakeJob(HookType.PostCompact, new PostCompactInput(_claudeSessionId, "manual")));

        (await QueryAsync(db => db.Jobs.CountAsync(j => j.Type == JobType.ExtractMemory))).Should().Be(0);
        (await QueryAsync(db => db.Jobs.CountAsync(j => j.Type == JobType.SummarizeSession))).Should().Be(1);
    }

    [Fact]
    public async Task SessionEnd_waits_while_an_earlier_hook_of_the_project_is_still_active()
    {
        var earlier = MakeJob(HookType.PostToolUse, ToolUse("Read"), createdAt: DateTimeOffset.UtcNow.AddSeconds(-5));
        earlier.Status = JobStatus.Pending;
        await using (var db = CreateContext())
        {
            await new JobRepository(db).EnqueueAsync(earlier, CancellationToken.None);
        }

        var end = MakeJob(HookType.SessionEnd, new SessionEndInput(_claudeSessionId, "logout"), createdAt: DateTimeOffset.UtcNow);
        var act = () => RunAsync(end);

        var deferred = (await act.Should().ThrowAsync<JobDeferredException>()).Which;
        deferred.RetryAt.Should().BeAfter(DateTimeOffset.UtcNow);
        (await QueryAsync(db => db.Sessions.CountAsync())).Should().Be(0, "nothing was applied while deferred");
        (await QueryAsync(db => db.HookInvocations.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task SessionEnd_stops_waiting_once_it_is_older_than_the_barrier_limit()
    {
        var earlier = MakeJob(HookType.PostToolUse, ToolUse("Read"), createdAt: DateTimeOffset.UtcNow.AddMinutes(-2));
        earlier.Status = JobStatus.Pending;
        await using (var db = CreateContext())
        {
            await new JobRepository(db).EnqueueAsync(earlier, CancellationToken.None);
        }

        var stale = MakeJob(HookType.SessionEnd, new SessionEndInput(_claudeSessionId, "logout"), createdAt: DateTimeOffset.UtcNow - ProcessHookJobHandler.MaxBarrierWait - TimeSpan.FromSeconds(5));

        await RunAsync(stale);

        (await QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync())).Status.Should().Be(SessionStatus.Ended);
    }

    [Fact]
    public async Task SessionEnd_does_not_wait_for_hooks_created_after_it_or_for_other_projects()
    {
        var otherProject = Guid.NewGuid();
        await using (var db = CreateContext())
        {
            db.Projects.Add(new Project { Id = otherProject, Name = "Other", RootPath = "/other", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            var repository = new JobRepository(db);
            var foreign = MakeJob(HookType.PostToolUse, ToolUse("Read"), createdAt: DateTimeOffset.UtcNow.AddSeconds(-5));
            foreign.ProjectId = otherProject;
            foreign.Status = JobStatus.Pending;
            await repository.EnqueueAsync(foreign, CancellationToken.None);
            var later = MakeJob(HookType.PostToolUse, ToolUse("Read"), createdAt: DateTimeOffset.UtcNow.AddSeconds(5));
            later.Status = JobStatus.Pending;
            await repository.EnqueueAsync(later, CancellationToken.None);
        }

        await RunAsync(MakeJob(HookType.SessionEnd, new SessionEndInput(_claudeSessionId, "logout")));

        (await QueryAsync(db => db.Sessions.AsNoTracking().SingleAsync())).Status.Should().Be(SessionStatus.Ended);
    }

    [Fact]
    public async Task A_notifier_failure_does_not_fail_the_job()
    {
        _notifier.Setup(n => n.NotifyAsync(It.IsAny<Guid>(), It.IsAny<HookInvocationEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("hub down"));

        await RunAsync(MakeJob(HookType.PostToolUse, ToolUse("Read")));

        (await QueryAsync(db => db.SessionEvents.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task A_job_without_a_project_is_rejected()
    {
        var job = MakeJob(HookType.PostToolUse, ToolUse("Read"));
        job.ProjectId = null;

        var act = () => RunAsync(job);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }
}
