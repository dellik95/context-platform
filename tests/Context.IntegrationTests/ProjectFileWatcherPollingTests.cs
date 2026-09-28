using System.Collections.Concurrent;
using Context.Application.Jobs;
using Context.Dashboard.Server.BackgroundServices;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Indexing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Context.IntegrationTests;

public sealed class ProjectFileWatcherPollingTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("watcher-polling-");
    private readonly ConcurrentQueue<string> _enqueued = new();
    private readonly Project _project;

    public ProjectFileWatcherPollingTests()
    {
        _project = new Project { Id = Guid.NewGuid(), Name = "polled", RootPath = _root.FullName, CreatedAt = DateTimeOffset.UtcNow };
        Write("src/existing.cs", "class Existing {}");
        Write("src/deleted.cs", "class Deleted {}");
        Write("node_modules/pkg/index.js", "module.exports = 1;");
    }

    [Fact]
    public async Task Polling_enqueues_created_changed_and_deleted_files_but_not_ignored_ones()
    {
        using var watcher = CreateWatcher();

        Write("src/created.cs", "class Created {}");
        Write("src/existing.cs", "class Existing { int changed; }");
        System.IO.File.Delete(Path.Combine(_root.FullName, "src/deleted.cs"));
        Write("node_modules/pkg/index.js", "module.exports = 2;");
        Write("node_modules/pkg/new.js", "module.exports = 3;");
        Write(".context/context.db", "data");

        var expected = new[] { "src/created.cs", "src/existing.cs", "src/deleted.cs" };
        await WaitUntilAsync(() => expected.All(_enqueued.Contains));
        await Task.Delay(TimeSpan.FromMilliseconds(800));

        _enqueued.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task Polling_does_not_enqueue_anything_for_an_unchanged_tree()
    {
        using var watcher = CreateWatcher();

        await Task.Delay(TimeSpan.FromMilliseconds(1000));

        _enqueued.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/mnt/c/Projects/Pet projects/claudy", "9p")]
    [InlineData("/mnt/c", "9p")]
    [InlineData("/mnt/cdrom/x", "ext4")]
    [InlineData("/home/dev/repo", "ext4")]
    [InlineData("/srv/my share/repo", "cifs")]
    public void GetFileSystemType_picks_the_longest_matching_mount_point(string path, string expected)
    {
        var procMounts = new[]
        {
            "/dev/sdd / ext4 rw,relatime 0 0",
            "none /mnt/wsl tmpfs rw,relatime 0 0",
            @"C:\134 /mnt/c 9p rw,noatime,aname=drvfs;path=C:\;uid=1000 0 0",
            @"//nas/share /srv/my\040share cifs rw 0 0",
        };

        MountFileSystem.GetFileSystemType(path, procMounts).Should().Be(expected);
    }

    private ProjectFileWatcher CreateWatcher()
    {
        var queue = new Mock<IJobQueue>();
        queue.Setup(q => q.EnqueueAsync(JobType.IndexFile, It.IsAny<object>(), It.IsAny<Guid?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((JobType _, object payload, Guid? _, int _, CancellationToken _) => _enqueued.Enqueue(((IndexFilePayload)payload).RelativePath))
            .ReturnsAsync(Guid.NewGuid());

        var services = new ServiceCollection().AddSingleton(queue.Object).BuildServiceProvider();

        return new ProjectFileWatcher(
            _project,
            services.GetRequiredService<IServiceScopeFactory>(),
            root => new GitignoreStyleMatcher(root),
            NullLogger.Instance,
            pollingInterval: TimeSpan.FromMilliseconds(100));
    }

    private void Write(string relativePath, string content)
    {
        var fullPath = Path.Combine(_root.FullName, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        System.IO.File.WriteAllText(fullPath, content);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the polling watcher should have picked the changes up by now");
            await Task.Delay(50);
        }
    }

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
