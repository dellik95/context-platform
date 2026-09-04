using Context.Dashboard.Server.Security;

namespace Context.Dashboard.Server.Api;

/// <summary>
/// <c>/api/fs/browse</c> — lets the Dashboard's "create project" folder picker navigate the local
/// filesystem instead of requiring the user to type an absolute path by hand. Admin-gated, not
/// per-project bearer-token gated: this is used specifically to pick a folder for a project that
/// doesn't exist yet, so there is no per-project token available at that point — same reasoning as
/// <c>ProjectEndpoints.CreateAsync</c>/<c>ListAsync</c> (see <see cref="AdminTokenAuth"/>).
/// </summary>
public static class FileSystemEndpoints
{
    public static void MapFileSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/api/fs/browse", Browse).RequireAdminToken();
    }

    private static IResult Browse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Results.Ok(BrowseRoots());
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return Results.BadRequest($"Invalid path: {ex.Message}");
        }

        if (!Directory.Exists(fullPath))
        {
            return Results.NotFound();
        }

        var directories = new List<DirectoryEntryDto>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(fullPath))
            {
                // Skip anything this process can't even see into — surfacing it just to have it
                // 404 the moment a user clicks in would be worse than silently omitting it.
                if (IsAccessible(dir))
                {
                    directories.Add(new DirectoryEntryDto(Path.GetFileName(dir), dir));
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Problem("Access denied to this directory.", statusCode: StatusCodes.Status403Forbidden);
        }

        directories.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        var parent = Path.GetPathRoot(fullPath) == fullPath ? null : Directory.GetParent(fullPath)?.FullName;
        return Results.Ok(new BrowseDirectoryResponse(fullPath, parent, directories));
    }

    /// <summary>Windows has no single filesystem root — start the picker at the drive list instead.</summary>
    private static BrowseDirectoryResponse BrowseRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new DirectoryEntryDto(d.Name, d.RootDirectory.FullName))
                .ToList();
            return new BrowseDirectoryResponse(Path: "", ParentPath: null, Directories: drives);
        }

        var directories = Directory.EnumerateDirectories("/")
            .Where(IsAccessible)
            .Select(dir => new DirectoryEntryDto(Path.GetFileName(dir), dir))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new BrowseDirectoryResponse("/", null, directories);
    }

    private static bool IsAccessible(string directoryPath)
    {
        try
        {
            // EnumerateDirectories on the candidate itself is the cheapest way to force the same
            // permission check a later browse-into would hit, without fully materializing its contents.
            using var enumerator = Directory.EnumerateDirectories(directoryPath).GetEnumerator();
            enumerator.MoveNext();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            // Reparse points / junctions that don't resolve, removable-media drives that vanished
            // mid-enumeration, etc. — not accessible either, but not a permissions error.
            return false;
        }
    }
}

public sealed record DirectoryEntryDto(string Name, string FullPath);

public sealed record BrowseDirectoryResponse(string Path, string? ParentPath, IReadOnlyList<DirectoryEntryDto> Directories);
