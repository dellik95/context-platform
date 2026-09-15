namespace Context.Memory.Extraction;

/// <summary>
/// Derives <see cref="Context.Domain.Entities.Memory.ScopePath"/> from a candidate's own
/// FilesModified/FilesRead lists — the longest common directory prefix, rather than asking the LLM
/// to name it separately (which risks hallucinating a path that doesn't match the files it just
/// listed). Files come from real repo paths already vetted by the caller (either the LLM, which is
/// constrained to choose from the session's actual file lists — see
/// <see cref="MemoryExtractionPromptBuilder"/> — or the offline heuristic path, which only ever
/// lists files that were genuinely changed), so a directory prefix derived from them is trustworthy.
/// </summary>
public static class ScopePathHeuristics
{
    /// <summary>
    /// The longest common containing directory of <paramref name="filesModified"/> (preferred) or,
    /// if empty, <paramref name="filesRead"/>. Null when there are no files, or when the only common
    /// ancestor is the repo root (too broad to call a "scope").
    /// </summary>
    public static string? DeriveFromFiles(IReadOnlyList<string> filesModified, IReadOnlyList<string> filesRead)
    {
        var files = filesModified.Count > 0 ? filesModified : filesRead;
        if (files.Count == 0)
        {
            return null;
        }

        var directories = files
            .Select(f => f.Replace('\\', '/'))
            .Select(f => f.LastIndexOf('/') is var i && i >= 0 ? f[..i] : string.Empty)
            .Where(d => d.Length > 0)
            .Select(d => d.Split('/'))
            .ToList();

        if (directories.Count == 0)
        {
            return null;
        }

        var common = directories[0];
        foreach (var dir in directories.Skip(1))
        {
            var max = Math.Min(common.Length, dir.Length);
            var matched = 0;
            while (matched < max && common[matched] == dir[matched])
            {
                matched++;
            }

            common = common[..matched];
            if (common.Length == 0)
            {
                return null;
            }
        }

        return common.Length > 0 ? string.Join('/', common) : null;
    }
}
