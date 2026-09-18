using System.Security.Cryptography;

namespace Context.Shared;

/// <summary>
/// Mints a random, URL-safe bearer token: 32 cryptographically random bytes, base64url-encoded
/// (no padding). Shared by every code path that generates a project's bearer token —
/// <c>ClaudeIntegrationInstaller</c> (the install path) and
/// Context.Dashboard.Server's <c>ProjectEndpoints.CreateAsync</c> (the project-creation path) — so both
/// produce tokens of the same shape from one implementation instead of two copies drifting apart.
/// The raw token is never persisted anywhere; only its <see cref="Hashing.HashToken"/> hash is.
/// </summary>
public static class BearerTokenGenerator
{
    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
