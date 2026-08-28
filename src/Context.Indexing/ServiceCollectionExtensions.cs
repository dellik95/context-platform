using Context.Indexing.Abstractions;
using Context.Indexing.Parsers;
using Microsoft.Extensions.DependencyInjection;

namespace Context.Indexing;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the default, zero-external-dependency Context.Indexing implementations:
    /// <see cref="FileContentHasher"/>, <see cref="FileSystemScanner"/>, and one
    /// <see cref="RegexHeuristicParser"/> per supported language family (resolved together as
    /// <c>IEnumerable&lt;ILanguageParser&gt;</c>; callers pick the one whose
    /// <see cref="ILanguageParser.SupportedExtensions"/> contains the file's extension).
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

        services.AddSingleton<ILanguageParser>(_ => new RegexHeuristicParser(CodeDialect.TypeScriptFamily));
        services.AddSingleton<ILanguageParser>(_ => new RegexHeuristicParser(CodeDialect.CSharp));
        services.AddSingleton<ILanguageParser>(_ => new RegexHeuristicParser(CodeDialect.Python));

        services.AddSingleton<Func<string, IIgnoreMatcher>>(_ => rootPath => new GitignoreStyleMatcher(rootPath));

        return services;
    }
}
