using System.Runtime.CompilerServices;
using Context.Indexing.Abstractions;

namespace Context.Indexing;

/// <summary>
/// Recursively walks a project root, pruning ignored directories before descending into them
/// (so an ignored tree like <c>node_modules/</c> is never even listed), and skipping files that
/// look binary (a NUL byte within the first ~8000 bytes).
/// </summary>
public sealed class FileSystemScanner : IFileScanner
{
    private const int BinarySniffLength = 8000;

    public async IAsyncEnumerable<ScannedFile> ScanAsync(
        string rootPath, IIgnoreMatcher ignore, [EnumeratorCancellation] CancellationToken ct)
    {
        var fullRoot = Path.GetFullPath(rootPath);
        await foreach (var file in WalkAsync(fullRoot, fullRoot, ignore, ct).WithCancellation(ct))
            yield return file;
    }

    private static async IAsyncEnumerable<ScannedFile> WalkAsync(
        string rootPath, string currentDir, IIgnoreMatcher ignore, [EnumeratorCancellation] CancellationToken ct)
    {
        string[] entries;
        try
        {
            entries = Directory.GetFileSystemEntries(currentDir);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            yield break;
        }

        Array.Sort(entries, StringComparer.Ordinal); // deterministic traversal order

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var isDirectory = Directory.Exists(entry);
            var relativePath = Path.GetRelativePath(rootPath, entry).Replace('\\', '/');
            var checkedPath = isDirectory ? relativePath + "/" : relativePath;

            if (ignore.IsIgnored(checkedPath) || ignore.IsIgnored(relativePath))
                continue; // pruned early: an ignored directory is never descended into

            if (isDirectory)
            {
                await foreach (var file in WalkAsync(rootPath, entry, ignore, ct))
                    yield return file;
                continue;
            }

            long sizeBytes;
            try
            {
                var info = new FileInfo(entry);
                sizeBytes = info.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            bool isBinary;
            try
            {
                isBinary = await LooksBinaryAsync(entry, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (isBinary) continue;

            yield return new ScannedFile(relativePath, entry, sizeBytes);
        }
    }

    private static async Task<bool> LooksBinaryAsync(string absolutePath, CancellationToken ct)
    {
        var buffer = new byte[BinarySniffLength];
        await using var stream = new FileStream(
            absolutePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 4096, useAsync: true);
        var totalRead = 0;
        int read;
        while (totalRead < buffer.Length &&
               (read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct)) > 0)
        {
            totalRead += read;
        }

        for (var i = 0; i < totalRead; i++)
            if (buffer[i] == 0)
                return true;

        return false;
    }
}
