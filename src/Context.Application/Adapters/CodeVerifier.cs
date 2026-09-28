using Context.Infrastructure.Abstractions;
using Context.Memory.Abstractions;

namespace Context.Application.Adapters;

/// <summary>
/// <see cref="ICodeVerifier"/> over the indexed files and symbols. A path is found by exact or
/// trailing-segment match against the repo-relative index; it is refuted only when it names a
/// directory the index covers and an extension the index holds, so files the indexer skips by type
/// are never counted as missing. Mixed results (one claim found, another refuted) are unknown.
/// </summary>
/// <remarks>Scoped: the file index is loaded once per project per scope (one extraction job).</remarks>
public sealed class CodeVerifier(
    IFileRepository fileRepository,
    ISymbolRepository symbolRepository,
    IProjectRepository projectRepository) : ICodeVerifier
{
    private const int SymbolSearchLimit = 5;

    private readonly Dictionary<Guid, ProjectIndex?> _indexes = [];

    public async Task<CodeCheck> CheckAsync(Guid projectId, IReadOnlyCollection<string> paths, IReadOnlyCollection<string> symbols, CancellationToken ct)
    {
        if (await GetIndexAsync(projectId, ct).ConfigureAwait(false) is not { } index)
        {
            return CodeCheck.Unknown;
        }

        var found = new List<string>();
        var missing = new List<string>();

        foreach (var raw in paths)
        {
            if (index.ToRelative(raw) is not { } path)
            {
                continue;
            }

            if (index.Contains(path))
            {
                found.Add(path);
            }
            else if (index.CoversDirectoryOf(path))
            {
                missing.Add(path);
            }
        }

        foreach (var symbol in symbols.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal))
        {
            var matches = await symbolRepository.SearchByNameAsync(projectId, symbol, SymbolSearchLimit, ct).ConfigureAwait(false);
            if (matches.Any(s => s.Name == symbol || s.QualifiedName == symbol || s.QualifiedName.EndsWith("." + symbol, StringComparison.Ordinal)))
            {
                found.Add(symbol);
            }
        }

        return (found.Count, missing.Count) switch
        {
            (> 0, 0) => new CodeCheck(CodeCheckResult.Verified, "found in code: " + string.Join(", ", found)),
            (0, > 0) => new CodeCheck(CodeCheckResult.Refuted, "missing from indexed directories: " + string.Join(", ", missing)),
            _ => CodeCheck.Unknown,
        };
    }

    private async Task<ProjectIndex?> GetIndexAsync(Guid projectId, CancellationToken ct)
    {
        if (_indexes.TryGetValue(projectId, out var cached))
        {
            return cached;
        }

        ProjectIndex? index = null;
        if (await projectRepository.GetByIdAsync(projectId, ct).ConfigureAwait(false) is { } project)
        {
            var files = await fileRepository.ListByProjectAsync(projectId, includeDeleted: false, ct).ConfigureAwait(false);
            if (files.Count > 0)
            {
                index = new ProjectIndex(project.RootPath, files.Select(f => f.RelativePath));
            }
        }

        _indexes[projectId] = index;
        return index;
    }

    private sealed class ProjectIndex
    {
        private readonly string _root;
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _filesByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _extensions = new(StringComparer.OrdinalIgnoreCase);

        public ProjectIndex(string rootPath, IEnumerable<string> relativePaths)
        {
            _root = Normalize(rootPath).TrimEnd('/');
            foreach (var path in relativePaths.Select(Normalize))
            {
                if (!_files.Add(path))
                {
                    continue;
                }

                var name = FileName(path);
                if (!_filesByName.TryGetValue(name, out var sameName))
                {
                    _filesByName[name] = sameName = [];
                }

                sameName.Add(path);
                _extensions.Add(Path.GetExtension(path));
                for (var slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
                {
                    _directories.Add(path[..slash]);
                }
            }
        }

        /// <summary>Repo-relative form of <paramref name="raw"/>, or null for a path outside the project.</summary>
        public string? ToRelative(string raw)
        {
            var path = Normalize(raw.Trim().Trim('`', '"', '\''));
            if (path.Length == 0)
            {
                return null;
            }

            var rooted = path.StartsWith('/') || (path.Length > 2 && path[1] == ':' && path[2] == '/');
            if (!rooted)
            {
                return path;
            }

            return path.StartsWith(_root + "/", StringComparison.OrdinalIgnoreCase) ? path[(_root.Length + 1)..] : null;
        }

        public bool Contains(string path) =>
            _files.Contains(path)
            || (_filesByName.TryGetValue(FileName(path), out var sameName)
                && sameName.Any(f => f.EndsWith("/" + path, StringComparison.OrdinalIgnoreCase)));

        public bool CoversDirectoryOf(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash > 0 && _directories.Contains(path[..slash]) && _extensions.Contains(Path.GetExtension(path));
        }

        private static string FileName(string path) => path[(path.LastIndexOf('/') + 1)..];

        private static string Normalize(string path)
        {
            var normalized = path.Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
            {
                normalized = normalized[2..];
            }

            return normalized;
        }
    }
}
