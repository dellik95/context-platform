using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;

namespace Context.Shared;

/// <summary>
/// Resolves the single, persistent, OS-appropriate data directory Context.Dashboard.Server keeps the
/// shared database in — machine-level app data rather than any one project's own repo, since the
/// host serves multiple projects. Deliberately never
/// falls back to the OS temp directory — temp dirs are periodically cleared by the OS/container
/// runtime, which would risk silently losing a persistent memory database.
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
    /// otherwise an OS-appropriate default (<c>%LocalAppData%\ContextPlatform</c> on Windows;
    /// <c>$XDG_DATA_HOME/context-platform</c>, or <c>~/.local/share/context-platform</c> if
    /// <c>XDG_DATA_HOME</c> is unset, elsewhere). Does not create the directory — callers create it
    /// just before first use, same as <c>ContextHostOptions</c> does for its own dataDir.
    /// </summary>
    public static string ResolveDataDir(IConfiguration? configuration = null)
    {
        var overridden = configuration?[DataDirEnvVar];
        if (overridden is not { Length: > 0 })
        {
            overridden = Environment.GetEnvironmentVariable(DataDirEnvVar);
        }

        if (overridden is { Length: > 0 })
        {
            return overridden;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "ContextPlatform");
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (xdgDataHome is { Length: > 0 })
        {
            return Path.Combine(xdgDataHome, "context-platform");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home is not { Length: > 0 })
        {
            home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        }

        return Path.Combine(home, ".local", "share", "context-platform");
    }

    /// <summary>Convenience: <c>{ResolveDataDir(configuration)}/context.db</c>.</summary>
    public static string ResolveDbPath(IConfiguration? configuration = null) =>
        Path.Combine(ResolveDataDir(configuration), DbFileName);

    /// <summary>
    /// One-time migration-forward safety check, invoked once at startup by the caller. If
    /// <paramref name="newDbPath"/> doesn't exist yet, but an old-style
    /// <c>{legacyProjectRoot}/.context/context.db</c> does, COPIES (never moves — the legacy file is
    /// left in place as a safety net) it forward, so switching to the shared data dir doesn't silently
    /// orphan an existing memory database. Returns the legacy path it copied from so the caller can log
    /// the event via whatever logger is already in scope there (e.g. Context.Dashboard.Server's Serilog
    /// <c>Log.Information</c>); returns <c>null</c> when no migration was needed or possible, in which
    /// case the caller should not log anything.
    /// </summary>
    public static string? EnsureMigratedFromLegacyLocation(string newDbPath, string? legacyProjectRoot)
    {
        if (File.Exists(newDbPath) || legacyProjectRoot is not { Length: > 0 })
        {
            return null;
        }

        var legacyDbPath = Path.Combine(legacyProjectRoot, ".context", DbFileName);
        if (!File.Exists(legacyDbPath))
        {
            return null;
        }

        var newDataDir = Path.GetDirectoryName(newDbPath);
        if (newDataDir is { Length: > 0 })
        {
            Directory.CreateDirectory(newDataDir);
        }

        File.Copy(legacyDbPath, newDbPath);
        return legacyDbPath;
    }
}
