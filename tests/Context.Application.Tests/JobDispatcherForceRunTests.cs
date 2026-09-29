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

public sealed class JobDispatcherForceRunTests : IAsyncDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-dispatcher-tests-{Guid.NewGuid():N}.db");
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly HangingHandler _hanging = new(JobType.EmbedChunk);
    private readonly HangingHandler _quick = new(JobType.ExtractMemory, hangOnFirstCall: false);
    private readonly ServiceProvider _provider;
    private readonly JobDispatcher _dispatcher;

    public JobDispatcherForceRunTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ContextDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddScoped<IJobRepository, JobRepository>();
        services.AddSingleton<IJobHandler>(_hanging);
        services.AddSingleton<IJobHandler>(_quick);
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<ContextDbContext>().Database.Migrate();
        }

        _dispatcher = new JobDispatcher(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            options: Options.Create(new JobDispatcherOptions { WorkersPerProject = 1, SharedWorkers = 0 }));
    }

    [Fact]
    public async Task ForceRunAsync_cancels_a_hung_execution_and_reruns_the_job_as_a_fresh_attempt()
    {
        var jobId = await SeedAsync(JobType.EmbedChunk, attempts: 4);
        await _dispatcher.StartAsync(CancellationToken.None);
        await _hanging.FirstCallStarted.WaitAsync(WaitTimeout);

        await _dispatcher.ForceRunAsync(jobId);

        var job = await WaitForStatusAsync(jobId, JobStatus.Done);
        _hanging.Tokens.Should().HaveCount(2);
        _hanging.Tokens.First().IsCancellationRequested.Should().BeTrue();
        job.Attempts.Should().Be(1, "a forced run starts a fresh attempt budget");
    }

    [Fact]
    public async Task ForceRunAsync_runs_a_pending_job_even_when_its_lane_is_full()
    {
        await SeedAsync(JobType.EmbedChunk, priority: 10);
        var pendingId = await SeedAsync(JobType.ExtractMemory);
        await _dispatcher.StartAsync(CancellationToken.None);
        await _hanging.FirstCallStarted.WaitAsync(WaitTimeout);

        await _dispatcher.ForceRunAsync(pendingId);

        await WaitForStatusAsync(pendingId, JobStatus.Done);
        _hanging.Tokens.Should().ContainSingle().Which.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task ForceRunAsync_rejects_a_done_job()
    {
        var jobId = await SeedAsync(JobType.ExtractMemory, status: JobStatus.Done);
        await _dispatcher.StartAsync(CancellationToken.None);

        var act = () => _dispatcher.ForceRunAsync(jobId);

        (await act.Should().ThrowAsync<JobStatusConflictException>()).Which.ActualStatus.Should().Be(JobStatus.Done);
    }

    private async Task<Guid> SeedAsync(JobType type, int priority = 0, int attempts = 0, JobStatus status = JobStatus.Pending)
    {
        using var scope = _provider.CreateScope();
        var job = await scope.ServiceProvider.GetRequiredService<IJobRepository>().EnqueueAsync(new Job
        {
            Type = type,
            ProjectId = _projectId,
            PayloadJson = "{}",
            Status = status,
            Priority = priority,
            Attempts = attempts,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        return job.Id;
    }

    private async Task<Job> WaitForStatusAsync(Guid jobId, JobStatus status)
    {
        var deadline = DateTimeOffset.UtcNow + WaitTimeout;
        while (true)
        {
            using var scope = _provider.CreateScope();
            var job = await scope.ServiceProvider.GetRequiredService<IJobRepository>().GetByIdAsync(jobId, CancellationToken.None);
            if (job?.Status == status)
            {
                return job;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Job {jobId} never reached {status}; last seen {job?.Status}.");
            }

            await Task.Delay(50);
        }
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

    private sealed class HangingHandler(JobType type, bool hangOnFirstCall = true) : IJobHandler
    {
        private readonly TaskCompletionSource _firstCallStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public JobType Type => type;
        public ConcurrentQueue<CancellationToken> Tokens { get; } = new();
        public Task FirstCallStarted => _firstCallStarted.Task;

        public async Task HandleAsync(Job job, CancellationToken ct)
        {
            Tokens.Enqueue(ct);
            if (hangOnFirstCall && _firstCallStarted.TrySetResult())
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        }
    }
}
