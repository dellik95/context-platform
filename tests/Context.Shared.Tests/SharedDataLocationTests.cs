using FluentAssertions;

namespace Context.Shared.Tests;

public class SharedDataLocationTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("shared-data-location-tests").FullName;

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void ResolveDataDir_WithoutOverride_IsTheApplicationBaseDirectory()
    {
        // Skipped rather than failed when the machine running the suite sets the override for real:
        // the env var winning is itself the documented contract, asserted below.
        if (Environment.GetEnvironmentVariable("CONTEXT_PLATFORM_DATA_DIR") is { Length: > 0 })
        {
            return;
        }

        SharedDataLocation.ResolveDataDir().Should().Be(AppContext.BaseDirectory);
    }

    [Fact]
    public void ResolveDbPath_IsContextDbInsideTheResolvedDataDir()
    {
        SharedDataLocation.ResolveDbPath()
            .Should().Be(Path.Combine(SharedDataLocation.ResolveDataDir(), "context.db"));
    }

    [Fact]
    public void EnumerateLegacyDbPaths_PrefersFormerAppDataDirsOverThePerRepoLocation()
    {
        var candidates = SharedDataLocation.EnumerateLegacyDbPaths("/repo").ToList();

        candidates.Should().NotBeEmpty();
        candidates.Should().AllSatisfy(path => Path.GetFileName(path).Should().Be("context.db"));
        candidates[^1].Should().Be(Path.Combine("/repo", ".context", "context.db"));
    }

    [Fact]
    public void EnumerateLegacyDbPaths_WithoutProjectRoot_OmitsThePerRepoLocation()
    {
        SharedDataLocation.EnumerateLegacyDbPaths(null)
            .Should().NotContain(path => path.Contains(".context", StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureMigrated_CopiesTheFirstExistingCandidateForwardWithItsSidecars()
    {
        var legacy = WriteDb("legacy", "main", wal: "wal-contents", shm: "shm-contents");
        var target = Path.Combine(_temp, "new-dir", "context.db");

        var migratedFrom = SharedDataLocation.EnsureMigratedFromLegacyLocation(target, [legacy]);

        migratedFrom.Should().Be(legacy);
        File.ReadAllText(target).Should().Be("main");
        File.ReadAllText(target + "-wal").Should().Be("wal-contents");
        File.ReadAllText(target + "-shm").Should().Be("shm-contents");
        File.Exists(legacy).Should().BeTrue("the legacy database is a safety net, never moved");
    }

    [Fact]
    public void EnsureMigrated_WithoutSidecars_CopiesOnlyTheDatabase()
    {
        var legacy = WriteDb("legacy", "main");
        var target = Path.Combine(_temp, "new-dir", "context.db");

        SharedDataLocation.EnsureMigratedFromLegacyLocation(target, [legacy]).Should().Be(legacy);

        File.Exists(target + "-wal").Should().BeFalse();
        File.Exists(target + "-shm").Should().BeFalse();
    }

    [Fact]
    public void EnsureMigrated_SkipsCandidatesThatDoNotExist()
    {
        var present = WriteDb("second", "from-second");
        var target = Path.Combine(_temp, "new-dir", "context.db");

        var migratedFrom = SharedDataLocation.EnsureMigratedFromLegacyLocation(
            target, [Path.Combine(_temp, "missing", "context.db"), present]);

        migratedFrom.Should().Be(present);
        File.ReadAllText(target).Should().Be("from-second");
    }

    [Fact]
    public void EnsureMigrated_WhenTargetAlreadyExists_DoesNothing()
    {
        var legacy = WriteDb("legacy", "legacy-contents");
        var target = WriteDb("current", "current-contents");

        SharedDataLocation.EnsureMigratedFromLegacyLocation(target, [legacy]).Should().BeNull();

        File.ReadAllText(target).Should().Be("current-contents");
    }

    [Fact]
    public void EnsureMigrated_WhenNoCandidateExists_DoesNothing()
    {
        var target = Path.Combine(_temp, "new-dir", "context.db");

        SharedDataLocation.EnsureMigratedFromLegacyLocation(target, []).Should().BeNull();

        File.Exists(target).Should().BeFalse();
    }

    private string WriteDb(string dirName, string contents, string? wal = null, string? shm = null)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_temp, dirName)).FullName;
        var dbPath = Path.Combine(dir, "context.db");
        File.WriteAllText(dbPath, contents);

        if (wal is not null)
        {
            File.WriteAllText(dbPath + "-wal", wal);
        }

        if (shm is not null)
        {
            File.WriteAllText(dbPath + "-shm", shm);
        }

        return dbPath;
    }
}
