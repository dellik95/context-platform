using System.Security;
using Context.Dashboard.Server.Security;
using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Context.Retrieval.Abstractions;
using Context.Shared;
using Microsoft.Extensions.Logging;

namespace Context.Dashboard.Server.Mcp;

/// <summary>
/// Estimates tokens returned vs full-file token sizes and records best-effort MCP search savings.
/// </summary>
public sealed class McpSavingsTracker(
    CurrentProjectAccessor currentProject,
    IUsageRecorder usageRecorder,
    ILogger<McpSavingsTracker>? logger = null)
{
    public static int EstimateReturnedTokens(IEnumerable<SearchHit> hits) =>
        hits.Sum(h => TokenEstimator.Estimate(h.Label) + TokenEstimator.Estimate(h.Snippet ?? string.Empty));

    public static int EstimateReturnedTokens(IEnumerable<Symbol> symbols) =>
        symbols.Sum(s => TokenEstimator.Estimate(s.QualifiedName) + TokenEstimator.Estimate(s.DocComment ?? string.Empty));

    public async Task RecordSearchSavingsAsync(
        string purpose,
        int returnedTokens,
        IEnumerable<Guid> ids,
        Func<Guid, CancellationToken, Task<Context.Domain.Entities.File?>> resolveFile,
        CancellationToken ct)
    {
        try
        {
            var files = new Dictionary<Guid, Context.Domain.Entities.File>();
            foreach (var id in ids.Distinct())
            {
                var file = await resolveFile(id, ct).ConfigureAwait(false);
                if (file is not null)
                {
                    files[file.Id] = file;
                }
            }

            if (files.Count == 0)
            {
                return;
            }

            var fullFileTokens = files.Values.Sum(EstimateFileTokens);
            var savedTokens = Math.Max(0, fullFileTokens - returnedTokens);

            await usageRecorder.RecordAsync(new UsageRecord
            {
                ProjectId = currentProject.ProjectId,
                Purpose = purpose,
                Provider = string.Empty,
                SavedTokens = savedTokens,
                CreatedAt = DateTimeOffset.UtcNow
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Failed to record MCP search-tool token savings for {Purpose}.", purpose);
        }
    }

    private int EstimateFileTokens(Context.Domain.Entities.File file)
    {
        if (file.SizeBytes > 0)
        {
            return TokenEstimator.EstimateFromByteLength(file.SizeBytes);
        }

        try
        {
            var fullPath = Path.Combine(currentProject.Project.RootPath, file.RelativePath);
            return TokenEstimator.EstimateFromByteLength(new FileInfo(fullPath).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return 0;
        }
    }
}
