using System.Text.RegularExpressions;
using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Extraction;
using Context.Shared;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Memory.Consolidation;

/// <summary>
/// Identifies change-log memories eligible for retirement and clusters related live knowledge
/// items into candidate reconsolidation groups.
/// </summary>
internal static partial class ReconsolidationClusterer
{
    /// <summary>A change summary stored as a memory: explicit, pinned and contested memories are never
    /// touched, and a change-log title stays while one of its facts states more than a change.</summary>
    public static string? ChangeLogReason(DomainMemory memory, IEnumerable<MemoryEvidence> evidence)
    {
        if (memory.Pinned || memory.Status == MemoryStatus.Contested || evidence.Any(e => e.Source == EvidenceSource.Explicit))
        {
            return null;
        }

        if (ChangedFilesTitleRegex().IsMatch(memory.Title))
        {
            return "file-change summary";
        }

        return MemoryCandidateValidator.IsChangeLog(memory.Title, EntityExtractor.ParseList(memory.FactsJson))
            ? "change log without durable facts"
            : null;
    }

    /// <summary>
    /// Connected components of the relatedness graph, in creation order. An edge needs a shared strong
    /// entity or topic key, and either embedding similarity or a second shared strong entity; a
    /// component larger than <see cref="MemoryMergeOptions.ReconsolidationMaxGroupSize"/> is split along its most similar edges.
    /// </summary>
    public static List<List<ReconsolidationItem>> Cluster(IReadOnlyList<ReconsolidationItem> items, MemoryMergeOptions options)
    {
        var pairs = CollectPostingPairs(items);
        var edges = new List<(int A, int B, double Cosine)>();
        foreach (var (a, b) in pairs)
        {
            var cosine = Cosine(items[a], items[b]);
            if (cosine >= options.ReconsolidationMinSimilarity || items[a].Strong.Count(items[b].Strong.Contains) >= MemoryReconsolidator.EdgeMinSharedStrong)
            {
                edges.Add((a, b, cosine));
            }
        }

        var components = new DisjointSets(items.Count);
        foreach (var (a, b, _) in edges)
        {
            components.Union(a, b);
        }

        var clusters = new List<List<int>>();
        foreach (var component in components.Groups().Where(g => g.Count >= 2))
        {
            if (component.Count <= options.ReconsolidationMaxGroupSize)
            {
                clusters.Add(component);
                continue;
            }

            clusters.AddRange(SplitOversizedComponent(component, items.Count, edges, options.ReconsolidationMaxGroupSize));
        }

        return [.. clusters.OrderBy(c => c.Min()).Select(c => c.Order().Select(i => items[i]).ToList())];
    }

    public static double Cosine(ReconsolidationItem a, ReconsolidationItem b) =>
        a.Vector is not null && b.Vector is not null ? CosineSimilarity.Compute(a.Vector, b.Vector) : 0.0;

    private static HashSet<(int, int)> CollectPostingPairs(IReadOnlyList<ReconsolidationItem> items)
    {
        var postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var keys = items[i].TopicKey is { } topicKey ? items[i].Strong.Append($"topic:{topicKey}") : items[i].Strong;
            foreach (var key in keys)
            {
                if (!postings.TryGetValue(key, out var list))
                {
                    postings[key] = list = [];
                }

                list.Add(i);
            }
        }

        var pairs = new HashSet<(int, int)>();
        foreach (var list in postings.Values)
        {
            for (var a = 0; a < list.Count; a++)
            {
                for (var b = a + 1; b < list.Count; b++)
                {
                    pairs.Add((list[a], list[b]));
                }
            }
        }

        return pairs;
    }

    private static IEnumerable<List<int>> SplitOversizedComponent(
        List<int> component,
        int itemCount,
        List<(int A, int B, double Cosine)> edges,
        int maxGroupSize)
    {
        var members = component.ToHashSet();
        var split = new DisjointSets(itemCount);
        foreach (var (a, b, _) in edges.Where(e => members.Contains(e.A)).OrderByDescending(e => e.Cosine).ThenBy(e => e.A).ThenBy(e => e.B))
        {
            if (split.Find(a) != split.Find(b) && split.SizeOf(a) + split.SizeOf(b) <= maxGroupSize)
            {
                split.Union(a, b);
            }
        }

        return component.GroupBy(split.Find).Select(g => g.ToList()).Where(g => g.Count >= 2);
    }

    [GeneratedRegex(@"^\s*changed\s+\d+\s+files?\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChangedFilesTitleRegex();
}
