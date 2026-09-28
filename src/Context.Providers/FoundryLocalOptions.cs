namespace Context.Providers;

/// <summary>
/// Microsoft Foundry Local: an OpenAI-compatible server on this machine. It only serves models that
/// have been loaded first (<c>foundry model load &lt;alias&gt;</c>); a request for any other model is
/// rejected with 400 "Model '…' is not loaded".
/// </summary>
public sealed class FoundryLocalOptions
{
    // Foundry Local picks a new port on every start unless one is pinned: 'foundry config set port 5273'.
    public const string DefaultBaseUrl = "http://127.0.0.1:5273/v1";

    public const string DefaultEmbeddingModel = "qwen3-embedding-0.6b";

    public const string DefaultChatModel = "phi-3.5-mini";

    // Foundry Local ignores it, but the OpenAI client refuses to send a request without one.
    internal const string PlaceholderApiKey = "foundry-local";

    public string BaseUrl { get; set; } = DefaultBaseUrl;

    public string EmbeddingModel { get; set; } = DefaultEmbeddingModel;

    public string ChatModel { get; set; } = DefaultChatModel;

    // Foundry serves the OpenAI API under /v1, so a bare host:port (as 'foundry status' prints it) 404s on every call.
    internal static string ApiBaseUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        return new Uri(trimmed).AbsolutePath == "/" ? trimmed + "/v1" : trimmed;
    }

    internal static string Hint(Uri? endpoint, string model) =>
        $"Is Foundry Local running at {endpoint}, and has '{model}' been downloaded and loaded ('foundry model load {model}')?";
}
