using System.Text;

namespace Context.Memory.Consolidation;

internal static class TextTokens
{
    /// <summary>Lowercase alphanumeric tokens; apostrophes stay inside a token so "doesn't" survives.</summary>
    public static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return tokens;
        }

        var current = new StringBuilder();
        foreach (var raw in text)
        {
            var c = raw == '’' ? '\'' : char.ToLowerInvariant(raw);
            if (char.IsLetterOrDigit(c) || (c == '\'' && current.Length > 0))
            {
                current.Append(c);
                continue;
            }

            Flush();
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            tokens.Add(current.ToString().TrimEnd('\''));
            current.Clear();
        }
    }

    public static string Normalize(string? text) => string.Join(' ', Tokenize(text));

    public static double Jaccard<T>(IReadOnlySet<T> a, IReadOnlySet<T> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return 0;
        }

        var intersection = a.Count <= b.Count ? a.Count(b.Contains) : b.Count(a.Contains);
        return (double)intersection / (a.Count + b.Count - intersection);
    }
}
