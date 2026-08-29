using Context.Indexing.Abstractions;
using Context.Indexing.Parsers.TreeSitter;
using Microsoft.Extensions.DependencyInjection;

namespace Context.Indexing;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default Context.Indexing implementations: <see cref="FileContentHasher"/>,
    /// <see cref="FileSystemScanner"/>, and one real tree-sitter-backed
    /// <see cref="ILanguageParser"/> per supported language/grammar (resolved together as
    /// <c>IEnumerable&lt;ILanguageParser&gt;</c>; callers pick the one whose
    /// <see cref="ILanguageParser.SupportedExtensions"/> contains the file's extension) —
    /// <see cref="TreeSitterCSharpParser"/> (.cs), <see cref="TreeSitterTypeScriptParser"/> (.ts),
    /// <see cref="TreeSitterTsxParser"/> (.tsx), <see cref="TreeSitterJavaScriptParser"/>
    /// (.js/.jsx), and <see cref="TreeSitterPythonParser"/> (.py). Each wraps a native tree-sitter
    /// grammar (package <c>TreeSitter.DotNet</c>) rather than being zero-native-dependency, and each
    /// implements <see cref="IDisposable"/> to release its long-lived native
    /// <c>TreeSitter.Language</c> handle when the DI container shuts down.
    /// <c>Context.Indexing.Parsers.RegexHeuristicParser</c> (the original MVP heuristic substitute)
    /// remains in the codebase and fully unit-tested, but is no longer wired up here as the default
    /// for any extension — construct it directly if a caller specifically wants the regex-heuristic
    /// behavior instead.
    /// </summary>
    /// <remarks>
    /// <see cref="IIgnoreMatcher"/> is intentionally NOT registered as a plain singleton here:
    /// <see cref="GitignoreStyleMatcher"/> needs a project root path to locate .gitignore /
    /// .contextignore, which isn't known at container-build time. Instead a factory delegate is
    /// registered so callers can create one per project root on demand.
    /// </remarks>
    public static IServiceCollection AddContextIndexing(this IServiceCollection services)
    {
        services.AddSingleton<IContentHasher, FileContentHasher>();
        services.AddSingleton<IFileScanner, FileSystemScanner>();

        services.AddSingleton<ILanguageParser, TreeSitterCSharpParser>();
        services.AddSingleton<ILanguageParser, TreeSitterTypeScriptParser>();
        services.AddSingleton<ILanguageParser, TreeSitterTsxParser>();
        services.AddSingleton<ILanguageParser, TreeSitterJavaScriptParser>();
        services.AddSingleton<ILanguageParser, TreeSitterPythonParser>();

        services.AddSingleton<Func<string, IIgnoreMatcher>>(_ => rootPath => new GitignoreStyleMatcher(rootPath));

        return services;
    }
}
