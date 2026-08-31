using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Context.Providers;

/// <summary>
/// Shared host-fallback logic for <see cref="Llm.OllamaLlmProvider"/> and
/// <see cref="Embeddings.OllamaEmbeddingProvider"/>. Bare-metal <c>dotnet run</c> reaching a local
/// Ollama install has always worked on this codebase's dev machine; a container reaching the SAME
/// Ollama install has not, even after a specific Windows Firewall block rule on ollama.exe was
/// found and disabled (see docker-compose.yml's networking comments for the full history) - the
/// remaining gap is environment-specific (which VM backend, which network mode) and not something
/// application code can fully solve. This is the best available mitigation short of that: when
/// running inside a container, try a couple of well-known alternate hosts before giving up,
/// instead of hard-failing on whatever single BaseUrl happened to be configured.
/// </summary>
public sealed class OllamaHostResolver
{
    // host.docker.internal is Docker Desktop/Rancher Desktop's DNS alias for the host and is
    // already the recommended CONTEXT_OLLAMA_BASE_URL (see docker-compose.yml), so it's usually
    // identical to the configured candidate below and deduplicated away; 172.17.0.1 is the default
    // Docker bridge gateway on plain Linux Docker Engine. Other IPs seen during the investigation
    // (192.168.127.254 - Rancher Desktop's VM gateway, 172.29.48.1 - a WSL vEthernet address) vary
    // by backend/context and aren't stable enough to hardcode here.
    private static readonly string[] FallbackContainerHosts = ["host.docker.internal", "172.17.0.1"];

    // Bounds ONLY the speculative fallback candidates below, never the caller's own configured
    // host (which keeps the caller's normal HttpClient.Timeout) - a genuinely unreachable guess
    // should fail fast during this one-time discovery instead of hanging per candidate.
    private static readonly TimeSpan FallbackProbeTimeout = TimeSpan.FromSeconds(8);

    private readonly HttpClient _http;
    private readonly Uri _configuredBaseUri;
    private readonly string _providerLabel;
    private readonly ILogger _logger;
    private readonly bool _runningInContainer;
    private readonly SemaphoreSlim _resolveLock = new(1, 1);

    private volatile string? _resolvedBaseUrl;

    public OllamaHostResolver(HttpClient httpClient, string configuredBaseUrl, string providerLabel, ILogger? logger = null)
    {
        _http = httpClient;
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

    /// <summary>
    /// Sends one request, resolving (and caching) which candidate host actually works the first
    /// time this is called. Every call after that goes straight to the cached host - no re-probing
    /// on the success path, so a working setup pays zero extra latency once resolved.
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUrl,
        Func<HttpContent>? contentFactory,
        CancellationToken ct)
    {
        var resolved = _resolvedBaseUrl;
        if (resolved is not null)
        {
            return await SendToAsync(resolved, method, relativeUrl, contentFactory, ct).ConfigureAwait(false);
        }

        if (!_runningInContainer)
        {
            // No fallback candidates outside a container - preserve today's exact behavior
            // (single attempt, original exception surfaces untouched) for the bare-metal path
            // that's actually verified working.
            var response = await SendToAsync(_configuredBaseUri.AbsoluteUri, method, relativeUrl, contentFactory, ct)
                .ConfigureAwait(false);
            _resolvedBaseUrl = _configuredBaseUri.AbsoluteUri;
            return response;
        }

        await _resolveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var alreadyResolved = _resolvedBaseUrl;
            if (alreadyResolved is not null)
            {
                return await SendToAsync(alreadyResolved, method, relativeUrl, contentFactory, ct).ConfigureAwait(false);
            }

            return await ResolveAndSendAsync(method, relativeUrl, contentFactory, ct).ConfigureAwait(false);
        }
        finally
        {
            _resolveLock.Release();
        }
    }

    private async Task<HttpResponseMessage> ResolveAndSendAsync(
        HttpMethod method, string relativeUrl, Func<HttpContent>? contentFactory, CancellationToken ct)
    {
        var candidates = BuildCandidates();
        Exception? lastFailure = null;

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            var isConfiguredHost = i == 0;

            try
            {
                HttpResponseMessage response;
                if (isConfiguredHost)
                {
                    response = await SendToAsync(candidate, method, relativeUrl, contentFactory, ct).ConfigureAwait(false);
                }
                else
                {
                    using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    probeCts.CancelAfter(FallbackProbeTimeout);
                    response = await SendToAsync(candidate, method, relativeUrl, contentFactory, probeCts.Token).ConfigureAwait(false);
                }

                _resolvedBaseUrl = candidate;
                if (!isConfiguredHost)
                {
                    _logger.LogWarning(
                        "{Provider}: configured Ollama host {Configured} was unreachable from inside this container; " +
                        "falling back to {Candidate}, which responded. Caching it for the rest of this process's lifetime.",
                        _providerLabel, _configuredBaseUri, candidate);
                }

                return response;
            }
            catch (Exception ex) when (IsConnectionLevelFailure(ex, ct))
            {
                lastFailure = ex;
                _logger.LogWarning(
                    ex, "{Provider}: candidate Ollama host {Candidate} was unreachable, trying the next candidate.",
                    _providerLabel, candidate);
            }
        }

        throw new HttpRequestException(
            $"{_providerLabel}: could not reach Ollama at any candidate host ({string.Join(", ", candidates)}). " +
            "If this is a container talking to a host-installed Ollama, check the host firewall next " +
            "(see docker-compose.yml's networking comments).",
            lastFailure);
    }

    private List<string> BuildCandidates()
    {
        var candidates = new List<string>(1 + FallbackContainerHosts.Length) { _configuredBaseUri.AbsoluteUri };
        foreach (var host in FallbackContainerHosts)
        {
            var candidateUri = new UriBuilder(_configuredBaseUri) { Host = host }.Uri.AbsoluteUri;
            if (!candidates.Contains(candidateUri, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(candidateUri);
            }
        }

        return candidates;
    }

    private async Task<HttpResponseMessage> SendToAsync(
        string baseUrl, HttpMethod method, string relativeUrl, Func<HttpContent>? contentFactory, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(baseUrl), relativeUrl));
        if (contentFactory is not null)
        {
            request.Content = contentFactory();
        }

        return await _http.SendAsync(request, ct).ConfigureAwait(false);
    }

    // A real HTTP response (even an error status) means the host was reachable - that's not a
    // fallback trigger, it's a real failure the caller should see and report as-is. Only
    // connection-level failures (DNS, refused/unreachable, or a timeout that isn't the CALLER
    // cancelling) mean "try the next candidate".
    private static bool IsConnectionLevelFailure(Exception ex, CancellationToken callerCt) =>
        ex is HttpRequestException or SocketException
        || (ex is OperationCanceledException && !callerCt.IsCancellationRequested);
}
