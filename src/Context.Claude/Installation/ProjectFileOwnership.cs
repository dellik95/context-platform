using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Context.Claude.Installation;

/// <summary>
/// Hands installer-written files back to whoever owns the project directory. The host commonly runs
/// as root (the systemd unit has no <c>User=</c>), so without this every file it writes is
/// root-owned — and the two token files are also 0600, which leaves Claude Code (running as the
/// developer) unable to read its own bearer token: every hook exits silently and <c>.mcp.json</c>
/// never loads. No-op unless the process is root on a Unix host.
/// </summary>
internal static class ProjectFileOwnership
{
    [DllImport("libc", SetLastError = true)]
    private static extern uint geteuid();

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int lchown(string path, uint owner, uint group);

    /// <param name="paths">Files and directories to chown; a directory here is not descended into.</param>
    /// <param name="recursiveDirs">Directories chowned along with everything under them.</param>
    public static void MatchProjectRoot(
        string projectRoot, IEnumerable<string> paths, IEnumerable<string> recursiveDirs, ILogger logger)
    {
        if (OperatingSystem.IsWindows() || !IsRoot())
        {
            return;
        }

        var owner = TryGetOwner(projectRoot);
        if (owner is not { } ids || ids.Uid == 0)
        {
            return;
        }

        var targets = paths.Where(p => File.Exists(p) || Directory.Exists(p))
            .Concat(recursiveDirs.SelectMany(ExpandRecursive))
            .Distinct(StringComparer.Ordinal);

        foreach (var target in targets)
        {
            if (lchown(target, ids.Uid, ids.Gid) != 0)
            {
                logger.LogWarning(
                    "Could not chown {Path} to {Uid}:{Gid} (errno {Errno})",
                    target, ids.Uid, ids.Gid, Marshal.GetLastPInvokeError());
            }
        }
    }

    private static IEnumerable<string> ExpandRecursive(string path)
    {
        if (!Directory.Exists(path))
        {
            yield break;
        }

        yield return path;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            yield return entry;
        }
    }

    private static bool IsRoot()
    {
        try
        {
            return geteuid() == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    // .NET exposes no owner uid/gid, and struct stat's layout differs per libc/architecture, so
    // coreutils' stat is the portable way to read it. Install is rare enough that a process spawn
    // is irrelevant.
    private static (uint Uid, uint Gid)? TryGetOwner(string path)
    {
        try
        {
            var psi = new ProcessStartInfo("stat")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("%u:%g");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(path);

            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(5000) || process.ExitCode != 0)
            {
                return null;
            }

            var parts = output.Split(':');
            return parts.Length == 2
                && uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var uid)
                && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var gid)
                    ? (uid, gid)
                    : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
