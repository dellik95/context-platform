using System.Collections.Concurrent;
using Context.Application.Jobs;
using Context.Application.Jobs.Handlers;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Infrastructure;
using Context.Infrastructure.Abstractions;
using Context.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Context.Application.Tests;

public sealed class JobDispatcherWorkerPoolTests : IAsyncDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Well under JobDispatcher's 5s poll interval, so only a direct wake-up can meet it.</summary>
    private static readonly TimeSpan WakeTimeout = TimeSpan.FromSeconds(2);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-dispatcher-pool-{Guid.NewGuid():N}.db");
    private readonly Guid _projectA = Guid.NewGuid();
    private readonly Guid _projectB = Guid.NewGuid();
    private readonly GatedHandler _handler = new();
    private readonly CompletionHandler _lightHandler = new(JobType.ProcessHook);
    private readonly CompletionHandler _forgetHandler = new(JobType.ForgetMemory);
    private readonly ServiceProvider _provider;
    private readonly JobDispatcher _dispatcher;

    public JobDispatcherWorkerPoolTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ContextDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddScoped<IJobRepository, JobRepository>();
        services.AddSingleton<IJobHandler>(_handler);
        services.AddSingleton<IJobHandler>(_lightHandler);
        services.AddSingleton<IJobHandler>(_forgetHandler);
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ContextDbContext>().Database.Migrate();
        }

        _dispatcher = new JobDispatcher(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            options: Options.Create(new JobDispatcherOptions { WorkersPerProject = 1, SharedWorkers = 2 }));
    }

    [Fact]
    public async Task A_lone_project_runs_beyond_its_reserved_slots_while_nobody_else_needs_them()
    {
        var jobs = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            jobs.Add(await SeedAsync(_projectA, priority: 10 - i));
        }

        await _dispatcher.StartAsync(CancellationToken.None);

        await _handler.WaitStartedAsync(jobs[..3], WaitTimeout);
        _handler.HasStarted(jobs[3]).Should().BeFalse("one reserved plus two shared slots are all a lane may hold");

        _handler.Finish(jobs[0]);
        await _handler.WaitStartedAsync([jobs[3]], WaitTimeout);
    }

    [Fact]
    public async Task A_freed_shared_slot_goes_to_the_waiting_project_instead_of_back_to_the_one_over_its_share()
    {
        var aJobs = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            aJobs.Add(await SeedAsync(_projectA, priority: 10 - i));
        }

        await _dispatcher.StartAsync(CancellationToken.None);
        await _handler.WaitStartedAsync(aJobs[..3], WaitTimeout);

        var b1 = await _dispatcher.EnqueueAsync(JobType.EmbedChunk, new { }, _projectB, priority: 10);
        var b2 = await _dispatcher.EnqueueAsync(JobType.EmbedChunk, new { }, _projectB, priority: 5);
        await _handler.WaitStartedAsync([b1], WaitTimeout);
        _handler.HasStarted(b2).Should().BeFalse();

        // aJobs[1] was the second claim, i.e. A's first borrowed slot.
        _handler.Finish(aJobs[1]);

        await _handler.WaitStartedAsync([b2], WakeTimeout);
        _handler.HasStarted(aJobs[3]).Should().BeFalse("A is over its even share of the shared slots");
    }

    [Fact]
    public async Task Light_jobs_run_while_every_ordinary_slot_of_the_project_is_held_by_slow_jobs()
    {
        var heavy = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            heavy.Add(await SeedAsync(_projectA, priority: 10));
        }

        await _dispatcher.StartAsync(CancellationToken.None);
        await _handler.WaitStartedAsync(heavy, WaitTimeout);

        var hook = await _dispatcher.EnqueueAsync(JobType.ProcessHook, new { }, _projectA, JobPriority.Hook);
        var forget = await _dispatcher.EnqueueAsync(JobType.ForgetMemory, new { }, _projectA, JobPriority.Session);

        await _lightHandler.WaitHandledAsync(hook, WakeTimeout);
        await _forgetHandler.WaitHandledAsync(forget, WakeTimeout);
        heavy.Should().OnlyContain(id => !_handler.IsFinished(id), "the slow jobs are still holding their slots");
    }

    [Fact]
    public async Task Ordinary_jobs_never_take_a_light_lane_slot_so_a_light_backlog_cannot_be_starved_by_them()
    {
        // 1 reserved + 2 shared ordinary slots are held; a 4th ordinary job must wait even though the light slots are free.
        var heavy = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            heavy.Add(await SeedAsync(_projectA, priority: 10 - i));
        }

        await _dispatcher.StartAsync(CancellationToken.None);
        await _handler.WaitStartedAsync(heavy[..3], WaitTimeout);

        await Task.Delay(TimeSpan.FromMilliseconds(500));
        _handler.HasStarted(heavy[3]).Should().BeFalse("light slots are only for light jobs");
    }

    private async Task<Guid> SeedAsync(Guid projectId, int priority)
    {
        using var scope = _provider.CreateScope();
        var job = await scope.ServiceProvider.GetRequiredService<IJobRepository>().EnqueueAsync(new Job
        {
            Type = JobType.EmbedChunk,
            ProjectId = projectId,
            PayloadJson = "{}",
            Status = JobStatus.Pending,
            Priority = priority,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        return job.Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _dispatcher.DisposeAsync();
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    /// <summary>Completes at once and records which jobs it handled.</summary>
    private sealed class CompletionHandler(JobType type) : IJobHandler
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _handled = new();

        public JobType Type => type;

        public Task HandleAsync(Job job, CancellationToken ct)
        {
            Gate(job.Id).TrySetResult();
            return Task.CompletedTask;
        }

        public Task WaitHandledAsync(Guid jobId, TimeSpan timeout) => Gate(jobId).Task.WaitAsync(timeout);

        private TaskCompletionSource Gate(Guid jobId) =>
            _handled.GetOrAdd(jobId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }

    /// <summary>Each job runs until the test calls <see cref="Finish"/> for it.</summary>
    private sealed class GatedHandler : IJobHandler
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _started = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _finish = new();

        public JobType Type => JobType.EmbedChunk;

        public async Task HandleAsync(Job job, CancellationToken ct)
        {
            Gate(_started, job.Id).TrySetResult();
            await Gate(_finish, job.Id).Task.WaitAsync(ct);
        }

        public bool HasStarted(Guid jobId) => _started.TryGetValue(jobId, out var started) && started.Task.IsCompleted;

        public bool IsFinished(Guid jobId) => _finish.TryGetValue(jobId, out var finish) && finish.Task.IsCompleted;

        public Task WaitStartedAsync(IEnumerable<Guid> jobIds, TimeSpan timeout) =>
            Task.WhenAll(jobIds.Select(id => Gate(_started, id).Task)).WaitAsync(timeout);

        public void Finish(Guid jobId) => Gate(_finish, jobId).TrySetResult();

        private static TaskCompletionSource Gate(ConcurrentDictionary<Guid, TaskCompletionSource> gates, Guid jobId) =>
            gates.GetOrAdd(jobId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}
