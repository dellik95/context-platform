using System.Text.RegularExpressions;

namespace Context.Dashboard.Server.BackgroundServices;

/// <summary>
/// Finds which file system a path lives on, to decide whether inotify can watch it. On WSL2's
/// /mnt/c (9p/drvfs) inotify registers every directory over the 9p bridge, which can take many
/// minutes on a large tree, and never sees edits made from the Windows side at all; network mounts
/// miss remote edits the same way.
/// </summary>
internal static partial class MountFileSystem
{
    private static readonly HashSet<string> NoReliableEventsTypes = new(StringComparer.Ordinal)
    {
        "9p", "drvfs", "cifs", "smb3", "smbfs", "nfs", "nfs4", "vboxsf", "fuse.sshfs",
    };

    public static bool LacksReliableChangeEvents(string path) =>
        GetFileSystemType(path) is { } type && NoReliableEventsTypes.Contains(type);

    public static string? GetFileSystemType(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return GetFileSystemType(path, File.ReadLines("/proc/mounts"));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string? GetFileSystemType(string path, IEnumerable<string> procMountsLines)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd('/');
        string? bestType = null;
        var bestLength = -1;

        foreach (var line in procMountsLines)
        {
            var fields = line.Split(' ');
            if (fields.Length < 3)
            {
                continue;
            }

            var mountPoint = Unescape(fields[1]).TrimEnd('/');
            var covers = mountPoint.Length == 0
                || fullPath == mountPoint
                || fullPath.StartsWith(mountPoint + "/", StringComparison.Ordinal);

            // >= so a later mount stacked on the same point wins, as it does in the kernel.
            if (covers && mountPoint.Length >= bestLength)
            {
                bestLength = mountPoint.Length;
                bestType = fields[2];
            }
        }

        return bestType;
    }

    // /proc/mounts escapes space, tab, newline and backslash as \ooo octal.
    private static string Unescape(string field) =>
        OctalEscape().Replace(field, m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());

    [GeneratedRegex(@"\\([0-7]{3})")]
    private static partial Regex OctalEscape();
}
