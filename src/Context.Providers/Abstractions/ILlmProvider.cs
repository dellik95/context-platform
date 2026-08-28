namespace Context.Providers.Abstractions;

/// <summary>
/// A text-completion provider used for session summarization and memory extraction prompts.
/// </summary>
public interface ILlmProvider
{
    string Name { get; }

    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct);
}
