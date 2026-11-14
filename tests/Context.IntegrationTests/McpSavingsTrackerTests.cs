using Context.Dashboard.Server.Mcp;
using Context.Dashboard.Server.Security;
using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using DomainFile = Context.Domain.Entities.File;

namespace Context.IntegrationTests;

public sealed class McpSavingsTrackerTests
{
    [Fact]
    public async Task RecordSearchSavingsAsync_deduplicates_unchanged_file_within_cooldown_and_recounts_after_update_or_expiry()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        var ledger = new McpSavingsLedger(TimeSpan.FromMinutes(10), clock);

        var projectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var fileUpdatedAt = clock.GetUtcNow();
        var file = new DomainFile
        {
            Id = fileId,
            ProjectId = projectId,
            RelativePath = "src/Auth.cs",
            ContentHash = "h1",
            SizeBytes = 4000,
            IsDeleted = false,
            UpdatedAt = fileUpdatedAt
        };

        var project = new Project { Id = projectId, Name = "p", RootPath = "/repo", CreatedAt = fileUpdatedAt };
        var httpContext = new DefaultHttpContext();
        httpContext.Items[CurrentProjectAccessor.HttpContextItemKey] = project;
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns(httpContext);
        var currentProject = new CurrentProjectAccessor(accessor.Object);

        var recorded = new List<UsageRecord>();
        var usageRecorder = new Mock<IUsageRecorder>();
        usageRecorder.Setup(r => r.RecordAsync(It.IsAny<UsageRecord>(), It.IsAny<CancellationToken>()))
            .Callback<UsageRecord, CancellationToken>((rec, _) => recorded.Add(rec))
            .Returns(Task.CompletedTask);

        var tracker = new McpSavingsTracker(currentProject, usageRecorder.Object, ledger: ledger);
        Task<DomainFile?> Resolve(Guid _, CancellationToken __) => Task.FromResult<DomainFile?>(file);

        // 1st search counts full file savings.
        await tracker.RecordSearchSavingsAsync("McpCodeSearch", 50, [fileId], Resolve, CancellationToken.None);
        // 2nd search 1 minute later with unchanged file skips duplicate savings credit.
        clock.Advance(TimeSpan.FromMinutes(1));
        await tracker.RecordSearchSavingsAsync("McpCodeSearch", 50, [fileId], Resolve, CancellationToken.None);
        // 3rd search after file is re-indexed/modified credits again.
        file.UpdatedAt = clock.GetUtcNow().AddSeconds(5);
        await tracker.RecordSearchSavingsAsync("McpCodeSearch", 50, [fileId], Resolve, CancellationToken.None);
        // 4th search after cooldown window expires credits again.
        clock.Advance(TimeSpan.FromMinutes(11));
        await tracker.RecordSearchSavingsAsync("McpCodeSearch", 50, [fileId], Resolve, CancellationToken.None);

        recorded.Should().HaveCount(3);
        recorded.Should().OnlyContain(r => r.SavedTokens == 950);
    }

    private sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
