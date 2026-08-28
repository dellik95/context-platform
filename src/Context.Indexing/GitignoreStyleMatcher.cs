using System.Text;
using System.Text.RegularExpressions;
using Context.Indexing.Abstractions;

namespace Context.Indexing;

/// <summary>
/// <see cref="IIgnoreMatcher"/> implementation compiled from (in increasing precedence order):
/// <list type="number">
/// <item>a built-in structural denylist (VCS/build/dependency directories, common lockfiles),</item>
/// <item>the project's <c>.gitignore</c>,</item>
/// <item>the project's <c>.contextignore</c>,</item>
/// <item>any caller-supplied <paramref name="additionalPatterns"/>.</item>
/// </list>
/// On top of that, a built-in <b>secret-file</b> denylist (<c>.env*</c>, <c>*.pem</c>, <c>*.key</c>,
/// <c>*credentials*</c>) is checked as a hard, independent gate <i>before</i> the rules above and
/// cannot be relaxed by an ordinary <c>!</c> negation in <c>.gitignore</c>/<c>.contextignore</c> —
/// on purpose, so a stray negation pattern can never accidentally re-expose a secret file to the
/// indexer. The only way to change it is <paramref name="secretDenylistOverride"/>, an explicit
/// constructor argument the caller must deliberately pass (including an empty list, to disable the
/// gate entirely) — there is no boolean "disable secrets check" switch.
/// </summary>
public sealed class GitignoreStyleMatcher : IIgnoreMatcher
{
    private static readonly string[] DefaultDenylist =
    [
        ".git/", "node_modules/", "bin/", "obj/", ".venv/", "venv/", "dist/", "build/",
        ".angular/", ".next/", ".nuxt/", "__pycache__/", ".pytest_cache/", ".mypy_cache/",
        ".idea/", ".vs/", ".vscode/",
        // common lockfiles
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "composer.lock",
        "Gemfile.lock", "poetry.lock", "Cargo.lock", "packages.lock.json",
    ];

    private static readonly string[] DefaultSecretDenylist =
    [
        ".env*", "*.pem", "*.key", "*credentials*",
    ];

    private readonly List<CompiledPattern> _generalPatterns = [];
    private readonly List<CompiledPattern> _secretPatterns = [];

    /// <param name="rootPath">Absolute path to the project root; used to locate .gitignore/.contextignore.</param>
    /// <param name="additionalPatterns">Extra gitignore-syntax patterns layered on top (highest precedence).</param>
    /// <param name="secretDenylistOverride">
    /// When non-null, REPLACES the built-in secret-file denylist entirely (pass an empty array to
    /// disable secret-file protection outright). Must be passed explicitly and deliberately — there
    /// is no implicit or boolean way to turn this protection off.
    /// </param>
    public GitignoreStyleMatcher(
        string rootPath,
        IEnumerable<string>? additionalPatterns = null,
        IReadOnlyList<string>? secretDenylistOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        foreach (var pattern in DefaultDenylist)
            _generalPatterns.Add(CompilePattern(pattern));

        AppendPatternsFromFile(Path.Combine(rootPath, ".gitignore"));
        AppendPatternsFromFile(Path.Combine(rootPath, ".contextignore"));

        if (additionalPatterns is not null)
            foreach (var pattern in additionalPatterns)
                AppendPattern(pattern);

        foreach (var pattern in secretDenylistOverride ?? DefaultSecretDenylist)
            _secretPatterns.Add(CompilePattern(pattern));
    }

    public bool IsIgnored(string relativePath)
    {
        var normalized = Normalize(relativePath);
        if (normalized.Length == 0) return false;

        // Secret-file gate: independent of, and un-overridable by, general negation patterns.
        foreach (var secret in _secretPatterns)
            if (secret.Regex.IsMatch(normalized))
                return true;

        var ignored = false;
        foreach (var pattern in _generalPatterns)
            if (pattern.Regex.IsMatch(normalized))
                ignored = !pattern.IsNegation;

        return ignored;
    }

    private void AppendPatternsFromFile(string path)
    {
        if (!File.Exists(path)) return;
        foreach (var rawLine in File.ReadAllLines(path))
            AppendPattern(rawLine);
    }

    private void AppendPattern(string rawLine)
    {
        var line = rawLine.TrimEnd();
        if (line.Length == 0 || line.StartsWith('#')) return;
        _generalPatterns.Add(CompilePattern(line));
    }

    private static string Normalize(string path) =>
        path.Replace('\\', '/').Trim().TrimStart('/');

    private readonly record struct CompiledPattern(Regex Regex, bool IsNegation);

    /// <summary>
    /// Compiles a single .gitignore-syntax line into a regex matched against a normalized,
    /// root-relative, forward-slash path. Supports: literal path segments, <c>*</c> (any run of
    /// non-'/' characters), <c>**</c> (any run of characters, including '/'), a trailing <c>/</c>
    /// (directory pattern — matched the same as a plain path here, since <see cref="IsIgnored"/>
    /// is not told whether the path denotes a file or a directory; this is a known MVP
    /// simplification), and a leading <c>!</c> (negates a previous match).
    /// </summary>
    private static CompiledPattern CompilePattern(string rawPattern)
    {
        var pattern = rawPattern.Trim();
        var negate = pattern.StartsWith('!');
        if (negate) pattern = pattern[1..];

        if (pattern.EndsWith('/'))
            pattern = pattern[..^1];

        var anchored = pattern.StartsWith('/');
        if (anchored) pattern = pattern[1..];
        if (!anchored) anchored = pattern.Contains('/');

        var sb = new StringBuilder("^");
        if (!anchored) sb.Append("(?:.*/)?");

        var i = 0;
        while (i < pattern.Length)
        {
            if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                sb.Append(".*");
                i += 2;
                if (i < pattern.Length && pattern[i] == '/') i++;
            }
            else if (pattern[i] == '*')
            {
                sb.Append("[^/]*");
                i++;
            }
            else if (pattern[i] == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else
            {
                sb.Append(Regex.Escape(pattern[i].ToString()));
                i++;
            }
        }

        // Matching the pattern exactly, or as a directory prefix of a longer path, covers both
        // "this exact file/dir" and "everything under this ignored directory".
        sb.Append("(?:/.*)?$");

        return new CompiledPattern(new Regex(sb.ToString(), RegexOptions.Compiled), negate);
    }
}
