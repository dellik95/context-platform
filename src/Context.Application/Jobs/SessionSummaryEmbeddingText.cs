using Context.Domain.Entities;

namespace Context.Application.Jobs;

/// <summary>The text a session-summary chunk is embedded from; the summarize job and the vector repair must use the same form.</summary>
public static class SessionSummaryEmbeddingText
{
    /// <summary>Null when every field is blank, so there is nothing worth embedding.</summary>
    public static string? For(SessionSummaryChunk chunk)
    {
        var text = $"{chunk.Title}\n{chunk.Completed}\n{chunk.Discovered}\n{chunk.Decisions}\n{chunk.OpenIssues}\n{chunk.NextSteps}";
        return string.IsNullOrWhiteSpace(text.Replace("\n", string.Empty)) ? null : text;
    }
}
