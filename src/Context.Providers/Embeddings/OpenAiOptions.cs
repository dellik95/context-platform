namespace Context.Providers.Embeddings;

public sealed class OpenAiOptions
{
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "text-embedding-3-small";

    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
}
