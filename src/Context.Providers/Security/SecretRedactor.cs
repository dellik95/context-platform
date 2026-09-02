using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Context.Providers.Security;

/// <summary>
/// BACKLOG-08 / SEC-4: content-level secret scan applied to text right before it leaves this
/// machine for a non-local LLM/embedding provider (openai/anthropic). This is deliberately separate
/// from <c>GitignoreStyleMatcher</c> (which gates whole *files* out of the index by name, e.g.
/// <c>.env</c>, <c>*.pem</c>) -- that gate only stops obviously-named secret files from ever being
/// read. It does nothing for a secret pasted or hard-coded inside a file that otherwise looks like
/// ordinary source (a stray API key left in a comment, a token committed to a config file that
/// itself isn't excluded). This scan is the second, content-level layer: it runs on the actual
/// chunk text on the way to a cloud provider, and only redacts the matched span rather than the
/// whole chunk, so the rest of the content -- and therefore embedding/response quality -- is
/// unaffected.
///
/// No third-party secret-scanning service is used here by design (local-first tool philosophy,
/// and an explicit constraint on this change): everything below is a small, self-contained set of
/// regexes plus a Shannon-entropy heuristic for the generic case.
/// </summary>
public static class SecretRedactor
{
    public const string RedactedPlaceholder = "[REDACTED-SECRET]";

    /// <summary>
    /// Below this length a generic "key = value"-shaped match is far more likely to be a short
    /// placeholder (<c>apiKey = "test"</c>) than a real secret, so it's left alone rather than risk
    /// mangling ordinary sample code.
    /// </summary>
    private const int MinGenericSecretLength = 20;

    /// <summary>
    /// Real generated secrets (API keys, tokens) are close to uniformly random over their alphabet;
    /// English words and typical placeholder text sit well below this. 3.0 bits/char was chosen to
    /// sit clearly above placeholder-ish strings like "your-secret-key-here" while still catching
    /// base64/hex-shaped secrets.
    /// </summary>
    private const double MinEntropyBitsPerChar = 3.0;

    /// <summary>
    /// Common placeholder/sample markers. Any one of these appearing in a candidate value is treated
    /// as strong evidence it's not a real secret, regardless of length or entropy -- catches things
    /// like "your_api_key_goes_here_1234567890" that would otherwise pass the length/entropy bar.
    /// </summary>
    private static readonly string[] PlaceholderMarkers =
    [
        "your", "test", "example", "placeholder", "changeme", "change_me", "change-me", "xxx",
        "todo", "fixme", "dummy", "sample", "fake", "insert", "replace", "<", ">", "{{", "}}",
    ];

    // Whole-token, high-confidence provider-prefixed secrets: the prefix alone all but guarantees
    // this is a real credential, so the entire match is redacted (there's no separate "variable name"
    // portion worth preserving).
    private static readonly Regex OpenAiKeyPattern = new(@"sk-[A-Za-z0-9_-]{20,}", RegexOptions.Compiled);
    private static readonly Regex GitHubTokenPattern = new(@"gh[pou]_[A-Za-z0-9]{20,}", RegexOptions.Compiled);
    private static readonly Regex AwsAccessKeyIdPattern = new(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled);

    // Generic "credential-shaped variable assignment": <name-that-looks-like-a-credential> [:=] "<value>".
    // Group 1 is just the value, so only that span gets redacted -- the variable name and surrounding
    // syntax (e.g. `apiKey = "..."`) are preserved, per the "redact only the matched span" requirement.
    private static readonly Regex GenericCredentialAssignmentPattern = new(
        """\b(?:api[_-]?key|secret[_-]?key|secret|access[_-]?token|auth[_-]?token|token|password|passwd|pwd)\b\s*[:=]\s*["']([^"'\r\n]{1,200})["']""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Scans <paramref name="text"/> for secret-shaped substrings and replaces each one with
    /// <see cref="RedactedPlaceholder"/>. Returns the (possibly unchanged) text; <paramref name="redactionCount"/>
    /// reports how many spans were redacted so callers can decide whether to log.
    /// </summary>
    public static string Redact(string text, out int redactionCount)
    {
        if (string.IsNullOrEmpty(text))
        {
            redactionCount = 0;
            return text;
        }

        var count = 0;

        var result = OpenAiKeyPattern.Replace(text, _ => { count++; return RedactedPlaceholder; });
        result = GitHubTokenPattern.Replace(result, _ => { count++; return RedactedPlaceholder; });
        result = AwsAccessKeyIdPattern.Replace(result, _ => { count++; return RedactedPlaceholder; });
        result = GenericCredentialAssignmentPattern.Replace(result, match =>
        {
            var valueGroup = match.Groups[1];
            if (!LooksLikeSecret(valueGroup.Value))
            {
                return match.Value;
            }

            count++;
            var offsetInMatch = valueGroup.Index - match.Index;
            var prefix = match.Value[..offsetInMatch];
            var suffix = match.Value[(offsetInMatch + valueGroup.Length)..];
            return prefix + RedactedPlaceholder + suffix;
        });

        redactionCount = count;
        return result;
    }

    /// <summary>
    /// Convenience overload that also logs a warning (identifying the content by <paramref name="contextId"/>
    /// -- e.g. a chunk id or a hash, never the secret itself) when at least one redaction happened.
    /// </summary>
    public static string Redact(string text, string contextId, ILogger logger)
    {
        var result = Redact(text, out var count);
        if (count > 0)
        {
            logger.LogWarning(
                "Redacted {Count} likely secret(s) from content before sending it to an external provider (context: {ContextId}).",
                count, contextId);
        }

        return result;
    }

    private static bool LooksLikeSecret(string candidate)
    {
        if (candidate.Length < MinGenericSecretLength)
        {
            return false;
        }

        foreach (var marker in PlaceholderMarkers)
        {
            if (candidate.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return ShannonEntropyBitsPerChar(candidate) >= MinEntropyBitsPerChar;
    }

    private static double ShannonEntropyBitsPerChar(string value)
    {
        var counts = new Dictionary<char, int>();
        foreach (var c in value)
        {
            counts[c] = counts.GetValueOrDefault(c) + 1;
        }

        var entropy = 0.0;
        foreach (var occurrences in counts.Values)
        {
            var p = (double)occurrences / value.Length;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }
}
