using System.Text.Json;
using System.Text.RegularExpressions;
using Context.Memory.Extraction;

namespace Context.Memory.Consolidation;

/// <param name="Key">Lowercase "kind:value", the matching form.</param>
/// <param name="Display">The value as first written.</param>
public sealed record ExtractedEntity(string Key, string Display);

/// <summary>
/// Pulls the concrete names a memory is about out of its text and file lists: ticket keys,
/// CONST_CASE config keys, multi-hump identifiers, backticked tokens, file basenames and the
/// producer's own ENTITIES. Ticket, symbol, config and file keys are strong (they identify a
/// subject); term keys are weak and never used to find neighbours.
/// </summary>
public static partial class EntityExtractor
{
    public const string Ticket = "ticket";
    public const string Symbol = "symbol";
    public const string Config = "config";
    public const string File = "file";
    public const string Term = "term";

    public const int MaxEntities = 40;
    private const int MaxTextIdentifiers = 10;
    private const int MaxValueLength = 200;

    private static readonly HashSet<string> NonTicketPrefixes = new(StringComparer.Ordinal)
    {
        "UTF", "SHA", "ISO", "AES", "RSA", "TLS", "SSL", "HTTP", "HTTPS", "CVE", "GPT", "RFC", "ECMA", "PKCS", "MD", "ES",
    };

    // Brand and technology names that look like identifiers but name nothing specific to a repo.
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "javascript", "typescript", "github", "gitlab", "graphql", "powershell", "nodejs", "macos", "ios", "oauth",
        "oauth2", "dynamodb", "postgresql", "mysql", "mongodb", "nuget", "devops", "openai", "chatgpt", "youtube",
        "linkedin", "iphone", "ipad", "websocket", "websockets", "openapi", "sqlite", "localhost", "ipv4", "ipv6",
    };

    private static readonly HashSet<string> KnownExtensions = new(StringComparer.Ordinal)
    {
        "cs", "csproj", "sln", "slnx", "ts", "tsx", "js", "jsx", "mjs", "cjs", "json", "yml", "yaml", "md", "go", "py",
        "rb", "java", "kt", "sql", "xml", "config", "props", "targets", "sh", "ps1", "html", "scss", "css", "toml",
        "ini", "env", "tf", "proto", "graphql", "gql", "txt", "lock",
    };

    private static readonly string[] Kinds = [Ticket, Symbol, Config, File, Term];

    public static bool IsStrong(string key) => KindOf(key) is Ticket or Symbol or Config or File;

    public static HashSet<string> StrongKeys(IEnumerable<string> keys) => keys.Where(IsStrong).ToHashSet(StringComparer.Ordinal);

    public static string KindOf(string key)
    {
        var colon = key.IndexOf(':');
        return colon > 0 ? key[..colon] : Term;
    }

    public static IReadOnlyList<ExtractedEntity> Extract(MemoryCandidate candidate) =>
        Build(
            [candidate.Title, candidate.Body, .. candidate.Facts],
            [.. candidate.FilesRead, .. candidate.FilesModified],
            candidate.Entities);

    public static IReadOnlyList<ExtractedEntity> Extract(Context.Domain.Entities.Memory memory) =>
        Build(
            [memory.Title, memory.Body, .. ParseList(memory.FactsJson)],
            [.. ParseList(memory.FilesReadJson), .. ParseList(memory.FilesModifiedJson)],
            []);

    public static IReadOnlyList<ExtractedEntity> ExtractFromText(string? text) => Build([text ?? ""], [], []);

    /// <summary>Paths with a known file extension written in <paramref name="text"/>, as written
    /// (line suffixes like ":42" dropped).</summary>
    public static IReadOnlyList<string> ExtractPaths(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return [.. PathRegex().Matches(text)
            .Select(m => m.Value)
            .Where(HasKnownExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    internal static IReadOnlyList<string> ParseList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<ExtractedEntity> Build(IReadOnlyList<string> texts, IReadOnlyList<string> files, IReadOnlyList<string> declared)
    {
        var result = new List<ExtractedEntity>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string kind, string display)
        {
            display = display.Trim();
            if (display.Length == 0 || result.Count >= MaxEntities)
            {
                return;
            }

            if (display.Length > MaxValueLength)
            {
                display = display[..MaxValueLength];
            }

            var key = $"{kind}:{display.ToLowerInvariant()}";
            if (seen.Add(key))
            {
                result.Add(new ExtractedEntity(key, display));
            }
        }

        foreach (var value in declared)
        {
            if (Classify(value) is { } entity)
            {
                Add(entity.Kind, entity.Display);
            }
        }

        var text = string.Join('\n', texts.Where(t => !string.IsNullOrWhiteSpace(t)));

        foreach (Match match in TicketRegex().Matches(text))
        {
            if (!NonTicketPrefixes.Contains(match.Value[..match.Value.IndexOf('-')]))
            {
                Add(Ticket, match.Value);
            }
        }

        foreach (var file in files)
        {
            Add(File, Basename(file));
        }

        foreach (var path in ExtractPaths(text))
        {
            Add(File, Basename(path));
        }

        // Paths and URLs are removed first so their directory segments don't read as symbols or keys.
        var prose = PathLikeRegex().Replace(text, " ");

        foreach (Match match in ConstCaseRegex().Matches(prose))
        {
            Add(Config, match.Value);
        }

        foreach (Match match in BacktickRegex().Matches(text))
        {
            if (ClassifyToken(match.Groups[1].Value) is { } entity)
            {
                Add(entity.Kind, entity.Display);
            }
        }

        var identifiers = 0;
        foreach (Match match in IdentifierRegex().Matches(prose))
        {
            if (identifiers >= MaxTextIdentifiers)
            {
                break;
            }

            if (IsMultiHumpIdentifier(match.Value) && !CommonWords.Contains(match.Value))
            {
                var before = result.Count;
                Add(Symbol, match.Value);
                if (result.Count > before)
                {
                    identifiers++;
                }
            }
        }

        return result;
    }

    /// <summary>A producer-declared value: an explicit "kind:value", or classified like a backticked token.</summary>
    private static (string Kind, string Display)? Classify(string raw)
    {
        var value = raw.Trim();
        var colon = value.IndexOf(':');
        if (colon > 0 && Kinds.Contains(value[..colon].Trim().ToLowerInvariant()) && colon < value.Length - 1)
        {
            var kind = value[..colon].Trim().ToLowerInvariant();
            var display = value[(colon + 1)..].Trim();
            return kind == File ? (File, Basename(display)) : (kind, display);
        }

        return ClassifyToken(value);
    }

    private static (string Kind, string Display)? ClassifyToken(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (value.Contains('/') || value.Contains('\\') || HasKnownExtension(value))
        {
            return value.Contains(' ') ? null : (File, Basename(value));
        }

        if (TicketFullRegex().IsMatch(value) && !NonTicketPrefixes.Contains(value[..value.IndexOf('-')]))
        {
            return (Ticket, value);
        }

        if (ConstCaseFullRegex().IsMatch(value))
        {
            return (Config, value);
        }

        if (!value.Contains(' '))
        {
            var token = value.Trim('!', '(', ')', ';', ',', '"', '\'', '-', '=', '<', '>');
            if (!IdentifierLikeRegex().IsMatch(token) || CommonWords.Contains(token))
            {
                return null;
            }

            var qualified = token.Contains('.') || token.Contains('_') || token.Contains("::", StringComparison.Ordinal);
            return qualified || IsMultiHumpIdentifier(token) ? (Symbol, token) : (Term, token);
        }

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= 3 && value.Length <= 40 && PlainWordsRegex().IsMatch(value)
            ? (Term, string.Join(' ', words))
            : null;
    }

    private static bool IsMultiHumpIdentifier(string token)
    {
        var humps = 1;
        var hasLower = false;
        for (var i = 0; i < token.Length; i++)
        {
            var c = token[i];
            hasLower |= char.IsLower(c);
            if (i > 0 && char.IsUpper(c) && (char.IsLower(token[i - 1]) || char.IsDigit(token[i - 1])))
            {
                humps++;
            }
        }

        return hasLower && humps >= 2;
    }

    private static bool HasKnownExtension(string value)
    {
        var dot = value.LastIndexOf('.');
        return dot > 0 && dot < value.Length - 1 && KnownExtensions.Contains(value[(dot + 1)..]);
    }

    private static string Basename(string path)
    {
        var trimmed = path.Trim().TrimEnd('/', '\\');
        var slash = trimmed.LastIndexOfAny(['/', '\\']);
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])[A-Z][A-Z0-9]+-\d+(?![A-Za-z0-9_])")]
    private static partial Regex TicketRegex();

    [GeneratedRegex(@"^[A-Z][A-Z0-9]+-\d+$")]
    private static partial Regex TicketFullRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+(?![A-Za-z0-9_])")]
    private static partial Regex ConstCaseRegex();

    [GeneratedRegex(@"^[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+$")]
    private static partial Regex ConstCaseFullRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])[A-Za-z][A-Za-z0-9]*(?![A-Za-z0-9_])")]
    private static partial Regex IdentifierRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?:(?:\.|::)[A-Za-z_][A-Za-z0-9_]*)*$")]
    private static partial Regex IdentifierLikeRegex();

    [GeneratedRegex(@"^[\w\- ]+$")]
    private static partial Regex PlainWordsRegex();

    [GeneratedRegex(@"`([^`\n]{1,80})`")]
    private static partial Regex BacktickRegex();

    [GeneratedRegex(@"(?<![\w.\-/\\])(?:[\w.\-]+[/\\])*[\w.\-]+\.[a-z0-9]+(?![\w/\\\-])")]
    private static partial Regex PathRegex();

    [GeneratedRegex(@"\S*[/\\]\S*")]
    private static partial Regex PathLikeRegex();
}
