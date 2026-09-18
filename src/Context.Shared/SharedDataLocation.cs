using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;

namespace Context.Shared;

/// <summary>
/// Resolves the single, persistent data directory Context.Dashboard.Server keeps the shared database
/// in — one database for every project the host serves, never any one project's own repo. Defaults to
/// the application's own install directory (<see cref="AppContext.BaseDirectory"/>) so the database
/// travels with the binaries. Deliberately never falls back to the OS temp directory — temp dirs are
/// periodically cleared by the OS/container runtime, which would risk silently losing a persistent
/// memory database.
/// </summary>
public static class SharedDataLocation
{
    /// <summary>Explicit override, highest priority. Read the same way <c>ContextHostOptions</c> reads
    /// its own <c>CONTEXT_PLATFORM_*</c> settings: via <see cref="IConfiguration"/> when the caller has
    /// one (which picks up the environment variable too, once <c>AddEnvironmentVariables()</c> is
    /// wired in), falling back to <see cref="Environment.GetEnvironmentVariable(string)"/> directly for
    /// callers — like today's CLI — that don't build an <see cref="IConfiguration"/> at all.</summary>
    private const string DataDirEnvVar = "CONTEXT_PLATFORM_DATA_DIR";

    private const string DbFileName = "context.db";

    /// <summary>
    /// Resolves the data directory. Priority: <c>CONTEXT_PLATFORM_DATA_DIR</c> verbatim if set;
    /// otherwise <see cref="AppContext.BaseDirectory"/>, the directory the host's own binaries were
    /// loaded from. Does not create the directory — callers create it just before first use, same as
    /// <c>ContextHostOptions</c> does for its own dataDir.
    /// </summary>
    /// <remarks>
    /// The env override exists precisely for installs where the binaries sit somewhere the process
    /// can't write (a Program Files-style location, a read-only container layer) — there is no
    /// silent fallback, so such a deployment must set <c>CONTEXT_PLATFORM_DATA_DIR</c> explicitly
    /// rather than have the database land somewhere the operator didn't choose.
    /// </remarks>
    public static string ResolveDataDir(IConfiguration? configuration = null)
    {
        var overridden = configuration?[DataDirEnvVar];
        if (overridden is not { Length: > 0 })
        {
            overridden = Environment.GetEnvironmentVariable(DataDirEnvVar);
        }

        return overridden is { Length: > 0 } ? overridden : AppContext.BaseDirectory;
    }

    /// <summary>
    /// The machine-level app-data directories this type used to default to, newest first. Retained
    /// only so <see cref="EnsureMigratedFromLegacyLocation"/> can carry an existing database forward
    /// out of them; nothing writes here any more.
    /// </summary>
    private static IEnumerable<string> FormerDefaultDataDirs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (localAppData is { Length: > 0 })
            {
                yield return Path.Combine(localAppData, "ContextPlatform");
            }

            yield break;
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (xdgDataHome is { Length: > 0 })
        {
            yield return Path.Combine(xdgDataHome, "context-platform");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home is not { Length: > 0 })
        {
            home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        }

        if (home is { Length: > 0 })
        {
            yield return Path.Combine(home, ".local", "share", "context-platform");
        }
    }

    /// <summary>Convenience: <c>{ResolveDataDir(configuration)}/context.db</c>.</summary>
    public static string ResolveDbPath(IConfiguration? configuration = null) =>
        Path.Combine(ResolveDataDir(configuration), DbFileName);

    /// <summary>
    /// One-time migration-forward safety check, invoked once at startup by the caller. If
    /// <paramref name="newDbPath"/> doesn't exist yet but a database does at one of the locations this
    /// host has previously defaulted to — a former machine-level app-data dir
    /// (<see cref="FormerDefaultDataDirs"/>), or the even older per-repo
    /// <c>{legacyProjectRoot}/.context/context.db</c> — COPIES (never moves — the legacy file is left
    /// in place as a safety net) the newest such database forward, so relocating the data dir doesn't
    /// silently orphan an existing memory database. Returns the legacy path it copied from so the
    /// caller can log the event via whatever logger is already in scope there (e.g.
    /// Context.Dashboard.Server's Serilog <c>Log.Information</c>); returns <c>null</c> when no
    /// migration was needed or possible, in which case the caller should not log anything.
    /// </summary>
    public static string? EnsureMigratedFromLegacyLocation(string newDbPath, string? legacyProjectRoot) =>
        EnsureMigratedFromLegacyLocation(newDbPath, EnumerateLegacyDbPaths(legacyProjectRoot));

    /// <summary>
    /// The above with the candidate list supplied explicitly, so tests can exercise the copy without
    /// depending on whether the machine running them happens to have a database sitting in one of the
    /// real former default directories. See <c>InternalsVisibleTo</c> in Context.Shared.csproj.
    /// </summary>
    internal static string? EnsureMigratedFromLegacyLocation(string newDbPath, IEnumerable<string> legacyDbPaths)
    {
        if (File.Exists(newDbPath))
        {
            return null;
        }

        var legacyDbPath = legacyDbPaths.FirstOrDefault(File.Exists);
        if (legacyDbPath is null)
        {
            return null;
        }

        var newDataDir = Path.GetDirectoryName(newDbPath);
        if (newDataDir is { Length: > 0 })
        {
            Directory.CreateDirectory(newDataDir);
        }

        // The -wal sidecar holds every commit not yet checkpointed back into the main file, so
        // copying context.db alone would silently drop the most recent writes. Carry the sidecars
        // across too and let SQLite recover from them on first open.
        File.Copy(legacyDbPath, newDbPath);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(legacyDbPath + suffix))
            {
                File.Copy(legacyDbPath + suffix, newDbPath + suffix);
            }
        }

        return legacyDbPath;
    }

    internal static IEnumerable<string> EnumerateLegacyDbPaths(string? legacyProjectRoot)
    {
        foreach (var formerDataDir in FormerDefaultDataDirs())
        {
            yield return Path.Combine(formerDataDir, DbFileName);
        }

        if (legacyProjectRoot is { Length: > 0 })
        {
            yield return Path.Combine(legacyProjectRoot, ".context", DbFileName);
        }
    }
}
