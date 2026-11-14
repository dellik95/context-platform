using Context.Application.Settings;
using Context.Domain.Enums;
using Context.Infrastructure.Abstractions;
using Context.Memory.Decay;
using Context.Memory.Search;
using Context.Providers;
using Context.Providers.Abstractions;
using Context.Providers.Embeddings;
using Context.Retrieval;
using Context.Retrieval.Abstractions;
using Context.Retrieval.Scoring;
using Context.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Context.Application.Engine;

/// <summary>
/// Orchestrates <see cref="IContextEngine.SearchContextAsync"/>: builds query-aware memory sections,
/// delegates pack assembly to <see cref="ContextPackBuilder"/>, and replaces snippets already
/// delivered in the active session with compact back-references.
/// </summary>
internal sealed class ContextPackDeliveryCoordinator(
    ContextPackBuilder contextPackBuilder,
    IMemoryRepository memoryRepository,
    IProjectSettingsService projectSettingsService,
    SessionDeliveryCache deliveryCache,
    MemorySearchHelper memorySearch)
{
    private const int MaxMemoryNotesPerSectionCap = 200;

    public async Task<ContextPack> SearchContextAsync(SearchQuery query, CancellationToken ct)
    {
        var (historicalNotes, openIssues) = await BuildMemorySectionsAsync(query, ct).ConfigureAwait(false);
        var pack = await contextPackBuilder.BuildAsync(query, historicalNotes, openIssues, ct).ConfigureAwait(false);

        return query.SessionKey is { Length: > 0 } sessionKey
            ? ApplySessionDeliveryCache(pack, query.ProjectId, sessionKey)
            : pack;
    }

    private async Task<(IReadOnlyList<string> HistoricalNotes, IReadOnlyList<string> OpenIssues)> BuildMemorySectionsAsync(
        SearchQuery query, CancellationToken ct)
    {
        var memories = await memoryRepository.ListByProjectAsync(query.ProjectId, includeSuperseded: false, ct).ConfigureAwait(false);
        if (memories.Count == 0)
        {
            return ([], []);
        }

        var settings = await projectSettingsService.GetAsync(query.ProjectId, ct).ConfigureAwait(false);
        var maxNotes = Math.Clamp(settings.MaxMemoryNotesPerSection, 1, MaxMemoryNotesPerSectionCap);

        var parsed = MemoryQuery.Parse(query.Text);
        var matches = await memorySearch.MatchAsync(query.ProjectId, parsed, memories, ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;

        bool IsCandidate(Context.Domain.Entities.Memory m) => !parsed.HasSubject || matches.ContainsKey(m.Id);
        double ScoreOf(Context.Domain.Entities.Memory m) => MemorySearchHelper.Score(m, matches.GetValueOrDefault(m.Id), now);

        var knowledge = memories
            .Where(m => m.Type != MemoryType.Issue && IsCandidate(m))
            .OrderBy(m => m.Status == MemoryStatus.Contested)
            .ThenByDescending(ScoreOf)
            .Take(maxNotes);

        var includeAllIssues = !parsed.HasSubject || QueryIntent.IsPendingWork(parsed.Text);
        var issues = memories
            .Where(m => m.Type == MemoryType.Issue && (includeAllIssues || matches.ContainsKey(m.Id)))
            .OrderByDescending(ScoreOf)
            .Take(maxNotes);

        return (
            RenderNotes([.. knowledge], (int)(query.TokenBudget * ContextPackBuilder.DefaultHistoricalReserveRatio)),
            RenderNotes([.. issues], (int)(query.TokenBudget * ContextPackBuilder.DefaultOpenIssuesReserveRatio)));
    }

    private static List<string> RenderNotes(IReadOnlyList<Context.Domain.Entities.Memory> memories, int budget)
    {
        var notes = memories.Select(m => MemoryNote.Format(m)).ToList();

        var kept = new List<int>();
        var used = 0;
        for (var i = 0; i < notes.Count; i++)
        {
            var cost = TokenEstimator.Estimate(notes[i]);
            if (used + cost <= budget)
            {
                kept.Add(i);
                used += cost;
            }
        }

        var spare = budget - used;
        foreach (var i in kept)
        {
            var withFacts = MemoryNote.Format(memories[i], includeFacts: true);
            var extra = TokenEstimator.Estimate(withFacts) - TokenEstimator.Estimate(notes[i]);
            if (withFacts.Length > notes[i].Length && extra <= spare)
            {
                notes[i] = withFacts;
                spare -= extra;
            }
        }

        return notes;
    }

    private ContextPack ApplySessionDeliveryCache(ContextPack pack, Guid projectId, string sessionKey)
    {
        if (pack.PrimaryHits.Count == 0)
        {
            return pack;
        }

        var newHits = new List<SearchHit>(pack.PrimaryHits.Count);
        var changed = false;

        foreach (var hit in pack.PrimaryHits)
        {
            var previousDelivery = deliveryCache.TryGetRecentDelivery($"{projectId:N}/{sessionKey}", hit.NodeId);
            if (previousDelivery is { } deliveredAt)
            {
                changed = true;
                var marker = $"[already shown to this session at {deliveredAt:O} - {hit.Kind} \"{hit.Label}\" (node {hit.NodeId}); call expand to see the full content again]";
                newHits.Add(hit with { Snippet = marker });
            }
            else
            {
                newHits.Add(hit);
            }
        }

        if (!changed)
        {
            return pack;
        }

        var estimatedTokens = TokenEstimator.Estimate(pack.ArchitectureSummary)
            + newHits.Sum(h => TokenEstimator.Estimate(h.Snippet ?? h.Label))
            + pack.HistoricalNotes.Sum(TokenEstimator.Estimate)
            + pack.OpenIssues.Sum(TokenEstimator.Estimate);

        return pack with { PrimaryHits = newHits, EstimatedTokens = estimatedTokens };
    }
}
