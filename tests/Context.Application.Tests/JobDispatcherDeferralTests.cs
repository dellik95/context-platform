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

public sealed class JobDispatcherDeferralTests : IAsyncDisposable
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(15);

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"context-dispatcher-deferral-{Guid.NewGuid():N}.db");
    private readonly DateTimeOffset _retryAt = DateTimeOffset.UtcNow.AddMinutes(10);
    private readonly ServiceProvider _provider;
    private readonly JobDispatcher _dispatcher;

    public JobDispatcherDeferralTests()
    {
        var services = new ServiceCollection();
        services.AddDbContext<ContextDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        services.AddScoped<IJobRepository, JobRepository>();
        services.AddSingleton<IJobHandler>(new DeferringHandler(_retryAt));
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
    public async Task A_deferred_job_waits_until_its_retry_time_without_spending_an_attempt()
    {
        var jobId = await SeedAsync(attempts: 5);
        await _dispatcher.StartAsync(CancellationToken.None);

        var job = await WaitForAsync(jobId, j => j.LastError is not null);

        job.Status.Should().Be(JobStatus.Pending, "waiting on earlier work must not fail a job on its last attempts");
        job.Attempts.Should().Be(5);
        job.NextRetryAt.Should().BeCloseTo(_retryAt, TimeSpan.FromSeconds(1));
        job.LastError.Should().Contain("earlier hooks");
    }

    [Fact]
    public async Task Wake_publishes_without_inserting_a_job()
    {
        await _dispatcher.StartAsync(CancellationToken.None);

        await _dispatcher.WakeAsync(Guid.NewGuid(), CancellationToken.None);

        using var scope = _provider.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IJobRepository>().ListByStatusAsync(JobStatus.Pending, null, CancellationToken.None))
            .Should().BeEmpty();
    }

    private async Task<Guid> SeedAsync(int attempts)
    {
        using var scope = _provider.CreateScope();
        var job = await scope.ServiceProvider.GetRequiredService<IJobRepository>().EnqueueAsync(new Job
        {
            Type = JobType.ProcessHook,
            ProjectId = Guid.NewGuid(),
            PayloadJson = "{}",
            Status = JobStatus.Pending,
            Attempts = attempts,
            CreatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        return job.Id;
    }

    private async Task<Job> WaitForAsync(Guid jobId, Func<Job, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow + WaitTimeout;
        while (true)
        {
            using var scope = _provider.CreateScope();
            var job = await scope.ServiceProvider.GetRequiredService<IJobRepository>().GetByIdAsync(jobId, CancellationToken.None);
            if (job is not null && predicate(job))
            {
                return job;
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException($"Job {jobId} never reached the expected state; last seen {job?.Status}.");
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

    private sealed class DeferringHandler(DateTimeOffset retryAt) : IJobHandler
    {
        public JobType Type => JobType.ProcessHook;

        public Task HandleAsync(Job job, CancellationToken ct) =>
            throw new JobDeferredException(retryAt, "waiting for earlier hooks");
    }
}
