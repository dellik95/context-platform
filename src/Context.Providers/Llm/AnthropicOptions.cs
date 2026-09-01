namespace Context.Providers.Llm;

public sealed class AnthropicOptions
{
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-3-5-sonnet-20241022";

    public string BaseUrl { get; set; } = "https://api.anthropic.com/v1";

    public int MaxTokens { get; set; } = 1024;
}
