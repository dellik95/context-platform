using System.Collections.Concurrent;
using System.Security;
using Context.Dashboard.Server.Security;
using Context.Domain.Entities;
using Context.Infrastructure.Abstractions;
using Context.Retrieval.Abstractions;
using Context.Shared;
using Microsoft.Extensions.Logging;

namespace Context.Dashboard.Server.Mcp;

/// <summary>
/// Tracks recently counted files across requests so repeated MCP searches hitting the same files
/// within a cooldown window do not repeatedly credit full-file read savings (unless the file was
/// modified since it was last credited).
/// </summary>
public sealed class McpSavingsLedger(TimeSpan? cooldown = null, TimeProvider? timeProvider = null)
{
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromMinutes(10);
    private const int MaxTrackedEntries = 10_000;

    private readonly TimeSpan _cooldown = cooldown ?? DefaultCooldown;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly ConcurrentDictionary<(Guid ProjectId, Guid FileId), Entry> _seen = new();

    private readonly record struct Entry(DateTimeOffset RecordedAt, DateTimeOffset FileUpdatedAt);

    public McpSavingsLedger() : this(null, null)
    {
    }

    public bool TryClaimFile(Guid projectId, Guid fileId, DateTimeOffset fileUpdatedAt)
    {
        var now = _time.GetUtcNow();
        var key = (projectId, fileId);

        while (true)
        {
            if (_seen.TryGetValue(key, out var existing))
            {
                var stillInCooldown = (now - existing.RecordedAt) < _cooldown && fileUpdatedAt <= existing.FileUpdatedAt;
                if (stillInCooldown)
                {
                    return false;
                }

                if (_seen.TryUpdate(key, new Entry(now, fileUpdatedAt), existing))
                {
                    return true;
                }

                continue;
            }

            if (_seen.Count >= MaxTrackedEntries)
            {
                PruneExpired(now);
            }

            if (_seen.TryAdd(key, new Entry(now, fileUpdatedAt)))
            {
                return true;
            }
        }
    }

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var pair in _seen)
        {
            if ((now - pair.Value.RecordedAt) >= _cooldown)
            {
                _seen.TryRemove(pair.Key, out _);
            }
        }

        if (_seen.Count >= MaxTrackedEntries)
        {
            _seen.Clear();
        }
    }
}

/// <summary>
/// Estimates tokens returned vs full-file token sizes and records best-effort MCP search savings.
/// </summary>
public sealed class McpSavingsTracker(
    CurrentProjectAccessor currentProject,
    IUsageRecorder usageRecorder,
    ILogger<McpSavingsTracker>? logger = null,
    McpSavingsLedger? ledger = null)
{
    private readonly McpSavingsLedger _ledger = ledger ?? new McpSavingsLedger();

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

            var newlyCounted = files.Values
                .Where(f => _ledger.TryClaimFile(currentProject.ProjectId, f.Id, f.UpdatedAt))
                .ToList();

            if (newlyCounted.Count == 0)
            {
                return;
            }

            var fullFileTokens = newlyCounted.Sum(EstimateFileTokens);
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
