using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Providers;

/// <summary>
/// Shared host-fallback logic for the Ollama-backed <see cref="Llm.OllamaLlmProvider"/> and
/// <see cref="Embeddings.OllamaEmbeddingProvider"/> — attached as an outer <see cref="DelegatingHandler"/>
/// on the <see cref="HttpClient"/> each one is constructed with, so it works underneath either
/// OllamaSharp's <c>OllamaApiClient</c> (which owns its own request building) instead of the
/// hand-rolled per-request sender this used to be.
///
/// Bare-metal <c>dotnet run</c> reaching a local Ollama install has always worked on this
/// codebase's dev machine; a container reaching the SAME Ollama install has not, even after a
/// specific Windows Firewall block rule on ollama.exe was found and disabled (see
/// docker-compose.yml's networking comments for the full history) — the remaining gap is
/// environment-specific (which VM backend, which network mode) and not something application code
/// can fully solve. This is the best available mitigation short of that: when running inside a
/// container, probe a couple of well-known alternate hosts before giving up, instead of
/// hard-failing on whatever single BaseUrl happened to be configured.
/// </summary>
public sealed class OllamaHostFallbackHandler : DelegatingHandler
{
    // host.docker.internal is Docker Desktop/Rancher Desktop's DNS alias for the host and is
    // already the recommended CONTEXT_OLLAMA_BASE_URL (see docker-compose.yml), so it's usually
    // identical to the configured candidate below and deduplicated away; 172.17.0.1 is the default
    // Docker bridge gateway on plain Linux Docker Engine. Other IPs seen during the investigation
    // (192.168.127.254 - Rancher Desktop's VM gateway, 172.29.48.1 - a WSL vEthernet address) vary
    // by backend/context and aren't stable enough to hardcode here.
    private static readonly string[] FallbackContainerHosts = ["host.docker.internal", "172.17.0.1"];

    // Bounds ONLY the speculative probe candidates below, never the real request that follows.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    private readonly Uri _configuredBaseUri;
    private readonly string _providerLabel;
    private readonly ILogger _logger;
    private readonly bool _runningInContainer;
    private readonly SemaphoreSlim _resolveLock = new(1, 1);

    private volatile Uri? _resolvedBaseUri;

    public OllamaHostFallbackHandler(string configuredBaseUrl, string providerLabel, ILogger? logger = null)
    {
        _configuredBaseUri = new Uri(configuredBaseUrl.TrimEnd('/') + "/");
        _providerLabel = providerLabel;
        _logger = logger ?? NullLogger.Instance;
        _runningInContainer = DetectContainer();
    }

    /// <summary>
    /// The standard cross-platform-safe container heuristic: Docker creates <c>/.dockerenv</c> in
    /// every Linux container it starts. <see cref="File.Exists"/> just returns false on Windows/
    /// bare metal - the path simply won't exist there - but the whole check is still wrapped
    /// because I/O calls can still surprise you in locked-down sandboxes.
    /// </summary>
    public static bool DetectContainer()
    {
        try
        {
            if (IsTruthy(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"))
                || IsTruthy(Environment.GetEnvironmentVariable("CONTEXT_RUNNING_IN_CONTAINER")))
            {
                return true;
            }

            return File.Exists("/.dockerenv");
        }
        catch
        {
            return false;
        }
    }

    private static bool IsTruthy(string? value) =>
        !string.IsNullOrEmpty(value) && (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var baseUri = await ResolveBaseUriAsync(cancellationToken).ConfigureAwait(false);
        request.RequestUri = RewriteHost(request.RequestUri!, baseUri);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Uri> ResolveBaseUriAsync(CancellationToken ct)
    {
        var resolved = _resolvedBaseUri;
        if (resolved is not null)
        {
            return resolved;
        }

        if (!_runningInContainer)
        {
            // No fallback candidates outside a container - preserve today's exact behavior
            // (single host, whatever error the real request hits surfaces untouched).
            _resolvedBaseUri = _configuredBaseUri;
            return _configuredBaseUri;
        }

        await _resolveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            resolved = _resolvedBaseUri;
            if (resolved is not null)
            {
                return resolved;
            }

            // A cheap, separate probe GET (rather than cloning/retrying the real request across
            // candidates) - equivalent for what this is actually detecting, since a connection-
            // level failure (DNS/refused/unreachable) is host-level, not path-level.
            foreach (var candidate in BuildCandidates())
            {
                var isConfiguredHost = candidate == _configuredBaseUri;
                if (isConfiguredHost || await ProbeAsync(candidate, ct).ConfigureAwait(false))
                {
                    _resolvedBaseUri = candidate;
                    if (!isConfiguredHost)
                    {
                        _logger.LogWarning(
                            "{Provider}: configured Ollama host {Configured} was unreachable from inside this container; " +
                            "falling back to {Candidate}, which responded. Caching it for the rest of this process's lifetime.",
                            _providerLabel, _configuredBaseUri, candidate);
                    }

                    return candidate;
                }
            }

            // No candidate probed successfully - use the configured host anyway so the real
            // request's own error (connection refused, timeout, etc.) is what the caller sees.
            _resolvedBaseUri = _configuredBaseUri;
            return _configuredBaseUri;
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    private async Task<bool> ProbeAsync(Uri candidate, CancellationToken ct)
    {
        try
        {
            using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probeCts.CancelAfter(ProbeTimeout);
            using var probeClient = new HttpClient();
            using var response = await probeClient.GetAsync(candidate, probeCts.Token).ConfigureAwait(false);
            return true; // any response (even an error status) proves the host is reachable.
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "{Provider}: candidate Ollama host {Candidate} was unreachable.", _providerLabel, candidate);
            return false;
        }
    }

    private List<Uri> BuildCandidates()
    {
        var candidates = new List<Uri>(1 + FallbackContainerHosts.Length) { _configuredBaseUri };
        foreach (var host in FallbackContainerHosts)
        {
            var candidate = new UriBuilder(_configuredBaseUri) { Host = host }.Uri;
            if (!candidates.Contains(candidate))
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    private static Uri RewriteHost(Uri requestUri, Uri baseUri) =>
        new UriBuilder(requestUri) { Scheme = baseUri.Scheme, Host = baseUri.Host, Port = baseUri.Port }.Uri;
}
