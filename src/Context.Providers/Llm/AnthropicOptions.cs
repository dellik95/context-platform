namespace Context.Providers.Llm;

/// <summary>
/// Configuration for <see cref="AnthropicLlmProvider"/>. Populate <see cref="ApiKey"/> from a
/// secret store or environment variable (e.g. ANTHROPIC_API_KEY) — never hardcode it.
/// </summary>
public sealed record AnthropicOptions
{
    public string? ApiKey { get; init; }

    public string Model { get; init; } = "claude-3-5-sonnet-20241022";

    public string BaseUrl { get; init; } = "https://api.anthropic.com/v1";

    public int MaxTokens { get; init; } = 1024;
}
