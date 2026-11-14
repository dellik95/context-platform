using System.Net;
using System.Text.RegularExpressions;

namespace Context.Indexing;

/// <summary>
/// Converts raw XML documentation comments captured from AST parsers into clean, readable plain text
/// suitable for full-text and vector indexing.
/// </summary>
public static partial class DocCommentFormatter
{
    /// <summary>
    /// Strips XML doc tags, expands cref/param attributes, and normalizes whitespace.
    /// Returns an empty string when no meaningful text remains.
    /// </summary>
    public static string ToPlainText(string? docComment)
    {
        if (string.IsNullOrWhiteSpace(docComment))
        {
            return string.Empty;
        }

        var text = docComment.Trim();
        text = StripLinePrefixes(text);

        // Preserve useful identifier names from <see cref="..."/> and <seealso cref="..."/> before stripping tags.
        text = SeeRefRegex().Replace(text, m => StripDocPrefix(m.Groups[1].Value));
        text = ParamRefRegex().Replace(text, m => m.Groups[1].Value);
        text = ParamTagRegex().Replace(text, m => $"\n{m.Groups[1].Value}: ");
        text = TypeParamTagRegex().Replace(text, m => $"\n{m.Groups[1].Value}: ");
        text = ReturnsTagRegex().Replace(text, "\nReturns: ");
        text = BlockTagRegex().Replace(text, "\n");
        text = AnyXmlTagRegex().Replace(text, string.Empty);

        text = WebUtility.HtmlDecode(text);
        text = MultiSpaceRegex().Replace(text, " ");
        text = MultiNewlineRegex().Replace(text, "\n");

        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => line.Length > 0);

        return string.Join('\n', lines).Trim();
    }

    private static string StripLinePrefixes(string raw)
    {
        var lines = raw.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("///", StringComparison.Ordinal))
            {
                line = line[3..].TrimStart();
            }
            else if (line.StartsWith("/**", StringComparison.Ordinal))
            {
                line = line[3..].TrimStart();
            }
            else if (line.EndsWith("*/", StringComparison.Ordinal))
            {
                line = line[..^2].TrimEnd();
            }
            else if (line.StartsWith('*'))
            {
                line = line[1..].TrimStart();
            }

            lines[i] = line;
        }

        return string.Join('\n', lines);
    }

    private static string StripDocPrefix(string cref)
    {
        if (cref.Length > 2 && cref[1] == ':' && char.IsLetter(cref[0]))
        {
            return cref[2..];
        }

        return cref;
    }

    [GeneratedRegex(@"<see(?:also)?\s+[^>]*?cref\s*=\s*[""']([^""']+)[""'][^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeeRefRegex();

    [GeneratedRegex(@"<(?:paramref|typeparamref)\s+[^>]*?name\s*=\s*[""']([^""']+)[""'][^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParamRefRegex();

    [GeneratedRegex(@"<param\s+[^>]*?name\s*=\s*[""']([^""']+)[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ParamTagRegex();

    [GeneratedRegex(@"<typeparam\s+[^>]*?name\s*=\s*[""']([^""']+)[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypeParamTagRegex();

    [GeneratedRegex(@"<returns\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReturnsTagRegex();

    [GeneratedRegex(@"</?(?:summary|remarks|example|para|br|list|item)\b[^>]*/?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex AnyXmlTagRegex();

    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"\n{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex MultiNewlineRegex();
}
