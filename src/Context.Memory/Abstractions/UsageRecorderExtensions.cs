using Context.Domain.Entities;
using Context.Providers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Context.Memory.Abstractions;

public static class UsageRecorderExtensions
{
    /// <summary>
    /// Best-effort: a failure recording token usage must never fail the work that produced it,
    /// so everything other than cancellation is logged and swallowed.
    /// </summary>
    public static async Task RecordChatUsageAsync(
        this IUsageRecorder usageRecorder,
        Guid projectId,
        Guid? sessionId,
        string purpose,
        IChatClient chatClient,
        UsageDetails? usage,
        ILogger? logger,
        CancellationToken ct)
    {
        try
        {
            await usageRecorder.RecordAsync(new UsageRecord
            {
                ProjectId = projectId,
                SessionId = sessionId,
                Purpose = purpose,
                Provider = chatClient.GetProviderName(),
                Model = chatClient.GetModelId(),
                PromptTokens = (int?)usage?.InputTokenCount,
                CompletionTokens = (int?)usage?.OutputTokenCount,
                TotalTokens = (int?)usage?.TotalTokenCount,
                CreatedAt = DateTimeOffset.UtcNow
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Failed to record token usage (purpose={Purpose}, project={ProjectId}).", purpose, projectId);
        }
    }
}
