using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using Microsoft.Extensions.Logging;

namespace Context.Memory.Consolidation;

/// <summary>Checks the files and symbols a claim names against the project's code; a failing verifier
/// degrades to Unknown so a code-index problem never blocks consolidation.</summary>
internal sealed class MemoryCodeChecker(ICodeVerifier codeVerifier, ILogger? logger)
{
    public const int MaxSymbolsToVerify = 5;

    public Task<CodeCheck> CheckAsync(Guid projectId, MemoryCandidate candidate, IEnumerable<ExtractedEntity> entities, CancellationToken ct)
    {
        var paths = candidate.FilesRead
            .Concat(candidate.FilesModified)
            .Concat(EntityExtractor.ExtractPaths(string.Join('\n', [candidate.Title, candidate.Body, .. candidate.Facts])))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return CheckAsync(projectId, paths, SymbolsToVerify(entities), ct);
    }

    public Task<CodeCheck> CheckAsync(Context.Domain.Entities.Memory memory, CancellationToken ct)
    {
        var facts = EntityExtractor.ParseList(memory.FactsJson);
        var paths = EntityExtractor.ParseList(memory.FilesReadJson)
            .Concat(EntityExtractor.ParseList(memory.FilesModifiedJson))
            .Concat(EntityExtractor.ExtractPaths(string.Join('\n', [memory.Title, memory.Body, .. facts])))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return CheckAsync(memory.ProjectId, paths, SymbolsToVerify(EntityExtractor.Extract(memory)), ct);
    }

    /// <summary>A Verified/Refuted row for <paramref name="check"/>, or null when it is Unknown or the
    /// latest code check in <paramref name="existing"/> already has that outcome.</summary>
    public static MemoryEvidence? Evidence(
        Guid projectId, Guid memoryId, CodeCheck check, IEnumerable<MemoryEvidence> existing, DateTimeOffset at)
    {
        EvidenceRelation? relation = check.Result switch
        {
            CodeCheckResult.Verified => EvidenceRelation.Verified,
            CodeCheckResult.Refuted => EvidenceRelation.Refuted,
            _ => null,
        };

        if (relation is not { } r || ConfidenceModel.LatestCodeCheck(existing) == r)
        {
            return null;
        }

        return new MemoryEvidence
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            MemoryId = memoryId,
            Relation = r,
            Source = EvidenceSource.Code,
            ObservedAt = at,
            Note = MemoryFields.Note(check.Note),
        };
    }

    private async Task<CodeCheck> CheckAsync(Guid projectId, IReadOnlyCollection<string> paths, IReadOnlyCollection<string> symbols, CancellationToken ct)
    {
        if (paths.Count == 0 && symbols.Count == 0)
        {
            return CodeCheck.Unknown;
        }

        try
        {
            return await codeVerifier.CheckAsync(projectId, paths, symbols, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "Code verification failed for project {ProjectId}; treating the claim as unverified.", projectId);
            return CodeCheck.Unknown;
        }
    }

    private static List<string> SymbolsToVerify(IEnumerable<ExtractedEntity> entities) =>
        [.. entities.Where(e => EntityExtractor.KindOf(e.Key) == EntityExtractor.Symbol).Select(e => e.Display).Take(MaxSymbolsToVerify)];
}
