using System.Text.Json;

namespace Context.Claude.Hooks;

/// <summary>
/// Lifts the file path out of a Claude Code tool-input payload (<c>tool_input</c>), which uses
/// snake_case keys the rest of the pipeline does not know about. Hooks record the result as a
/// first-class root-level <c>path</c> on the session event so
/// <c>Context.Memory.Summarization.PayloadFieldExtractor</c> stays a dumb root-level reader.
/// </summary>
public static class HookToolPath
{
    private static readonly string[] PathKeys = ["file_path", "notebook_path", "path", "filePath"];

    /// <summary>
    /// Repo-relative, forward-slashed path for <paramref name="toolInputJson"/>, or null when the
    /// payload carries none. Never throws: a malformed payload must not fail the hook.
    /// </summary>
    public static string? TryExtract(string? toolInputJson, string? projectRoot)
    {
        var raw = TryExtractRaw(toolInputJson);
        return raw is null ? null : Normalize(raw, projectRoot);
    }

    private static string? TryExtractRaw(string? toolInputJson)
    {
        if (string.IsNullOrWhiteSpace(toolInputJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(toolInputJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var key in PathKeys)
            {
                if (doc.RootElement.TryGetProperty(key, out var value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string Normalize(string path, string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
        {
            return path.Replace('\\', '/');
        }

        try
        {
            var relative = Path.GetRelativePath(projectRoot, path).Replace('\\', '/');

            // GetRelativePath returns the input unchanged for a path on another root, and "../…"
            // for one outside the project - keep the absolute form in both cases rather than
            // inventing a repo-relative path that does not exist.
            return relative.StartsWith("../", StringComparison.Ordinal) ? path.Replace('\\', '/') : relative;
        }
        catch (ArgumentException)
        {
            return path.Replace('\\', '/');
        }
    }
}
