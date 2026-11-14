using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Memory.Extraction;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Memory.Consolidation;

/// <summary>
/// Produces and validates per-cluster canonical merge and contradiction plans, using an LLM when
/// available or deterministic duplicate merging offline, and checks flagged contradictions against code.
/// </summary>
internal sealed class ReconsolidationPlanner(
    ICodeVerifier codeVerifier,
    IUsageRecorder usageRecorder,
    ILogger<MemoryReconsolidator>? logger)
{
    private readonly MemoryCodeChecker codeChecker = new(codeVerifier, logger);

    public async Task<ReconsolidationGroupPlan?> PlanWithModelAsync(
        Guid projectId,
        IChatClient chatClient,
        IReadOnlyList<ReconsolidationItem> cluster,
        MemoryMergeOptions options,
        CancellationToken ct)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, ReconsolidationPrompt.SystemPrompt),
            new(ChatRole.User, ReconsolidationPrompt.BuildUserPrompt([.. cluster.Select(i => i.Memory)], options.ReconsolidationSourceExcerptChars)),
        ];

        try
        {
            var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            await usageRecorder.RecordChatUsageAsync(projectId, null, "MemoryReconsolidation", chatClient, response.Usage, logger, ct).ConfigureAwait(false);
            return Validate(cluster, ReconsolidationPrompt.Parse(response.Text, cluster.Count), options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "LLM provider {Provider} failed to reconsolidate a memory group for project {ProjectId}; merging duplicates only.",
                chatClient.GetProviderName(), projectId);
            return null;
        }
    }

    /// <summary>Without a model only near-identical memories naming a common strong entity merge: the most
    /// confident one keeps its wording and gains the others' facts and files.</summary>
    public static ReconsolidationGroupPlan PlanOffline(IReadOnlyList<ReconsolidationItem> cluster, string note, MemoryMergeOptions options)
    {
        var duplicates = new DisjointSets(cluster.Count);
        for (var a = 0; a < cluster.Count; a++)
        {
            for (var b = a + 1; b < cluster.Count; b++)
            {
                if (ReconsolidationClusterer.Cosine(cluster[a], cluster[b]) >= MemoryRelationJudge.OfflineDuplicateCosine
                    && cluster[a].Strong.Overlaps(cluster[b].Strong)
                    && !MemoryRelationJudge.HasNegationAsymmetry(RelationSubject.From(cluster[a].Memory), RelationSubject.From(cluster[b].Memory)))
                {
                    duplicates.Union(a, b);
                }
            }
        }

        var canonicals = new List<ReconsolidationCanonical>();
        foreach (var component in duplicates.Groups().Where(g => g.Count >= 2))
        {
            var sources = component
                .Select(i => cluster[i])
                .OrderByDescending(i => i.Memory.Confidence)
                .ThenByDescending(i => i.Memory.LastReinforcedAt)
                .ToList();
            var facts = sources.Aggregate((IReadOnlyList<string>)[], (acc, s) => FactMerger.Merge(acc, s.Facts));
            var best = sources[0].Memory;
            var body = MergeVerifier.MissingNames(sources.Skip(1).Select(s => s.Memory.Body), [best.Body]).Count > 0
                ? MemoryMergePrompt.Fallback(sources.Select(s => s.Memory.Body))
                : best.Body;
            if (facts.Count > options.MaxFacts || body.Length > options.MaxBodyChars)
            {
                continue;
            }

            var candidate = new MemoryCandidate(best.Type, best.Title, body, EvidenceSource.Consolidation, facts, [], [], [], best.Topic);
            canonicals.Add(ToCanonical(candidate, sources));
        }

        return new ReconsolidationGroupPlan(canonicals, [], [], false, canonicals.Count > 0 ? note : null);
    }

    public async Task<List<ReconsolidationContradiction>> CheckContradictionsAsync(
        IReadOnlyList<(ReconsolidationItem A, ReconsolidationItem B, string? Reason)> contradictions,
        CancellationToken ct)
    {
        var checks = new Dictionary<Guid, CodeCheck>();
        var result = new List<ReconsolidationContradiction>(contradictions.Count);
        foreach (var (a, b, reason) in contradictions)
        {
            var left = await CheckAsync(a.Memory).ConfigureAwait(false);
            var right = await CheckAsync(b.Memory).ConfigureAwait(false);
            Guid? winner = (left.Result, right.Result) switch
            {
                (CodeCheckResult.Verified, CodeCheckResult.Refuted) => a.Memory.Id,
                (CodeCheckResult.Refuted, CodeCheckResult.Verified) => b.Memory.Id,
                _ => null,
            };
            var note = string.Join("; ", new[] { left.Note, right.Note }.OfType<string>());
            result.Add(new ReconsolidationContradiction(
                a.Memory.Id, b.Memory.Id, reason, new ContradictionCodeCheck(left.Result, right.Result, winner, note.Length > 0 ? note : null)));
        }

        return result;

        async Task<CodeCheck> CheckAsync(DomainMemory memory)
        {
            if (!checks.TryGetValue(memory.Id, out var check))
            {
                checks[memory.Id] = check = await codeChecker.CheckAsync(memory, ct).ConfigureAwait(false);
            }

            return check;
        }
    }

    /// <summary>
    /// Turns the model's answer into a plan. A fact or concrete name a canonical lost from one of its
    /// sources is put back, as long as the canonical stays within the fact and body limits; the group is
    /// ambiguous when it cannot (or when a canonical's body overruns the limit), when a source is claimed
    /// twice, or when the answer is malformed. A canonical backed by fewer than two sources merges
    /// nothing, so those sources stay as they are.
    /// </summary>
    private static ReconsolidationGroupPlan Validate(IReadOnlyList<ReconsolidationItem> cluster, ReconsolidationResponse response, MemoryMergeOptions options)
    {
        var problems = new List<string>(response.Problems);
        var canonicals = new List<ReconsolidationCanonical>();
        var merged = new HashSet<int>();

        for (var n = 0; n < response.Canonicals.Count; n++)
        {
            var parsed = response.Canonicals[n];
            var sourceIndexes = parsed.Sources.Where(s => s >= 1 && s <= cluster.Count).Select(s => s - 1).Distinct().ToList();
            if (sourceIndexes.Count < 2)
            {
                continue;
            }

            if (parsed.Candidate.Body.Trim().Length > options.MaxBodyChars)
            {
                problems.Add($"canonical {n + 1} has a body over the {options.MaxBodyChars}-character limit");
                continue;
            }

            var candidate = MemoryCandidateValidator.Normalize(parsed.Candidate, out var reason, options.MaxFacts, options.MaxBodyChars);
            if (candidate is null)
            {
                problems.Add($"canonical {n + 1} is not knowledge ({reason})");
                continue;
            }

            foreach (var index in sourceIndexes.Where(i => !merged.Add(i)))
            {
                problems.Add($"source {index + 1} is merged into more than one canonical");
            }

            var sources = sourceIndexes.Select(i => cluster[i]).ToList();
            if (Restore(candidate, sources, options, n + 1, problems) is { } restored)
            {
                canonicals.Add(ToCanonical(restored, sources));
            }
        }

        var contradictions = CollectContradictions(cluster, response, merged, problems);
        var keepSeparate = response.KeepSeparate.Where(i => i >= 1 && i <= cluster.Count).Select(i => i - 1).ToList();
        foreach (var index in keepSeparate.Where(merged.Contains))
        {
            problems.Add($"source {index + 1} is both merged and kept separate");
        }

        return new ReconsolidationGroupPlan(
            canonicals,
            contradictions,
            [.. keepSeparate.Select(i => cluster[i].Memory.Id)],
            problems.Count > 0,
            problems.Count > 0 ? string.Join("; ", problems.Distinct()) : null);
    }

    private static List<(ReconsolidationItem A, ReconsolidationItem B, string? Reason)> CollectContradictions(
        IReadOnlyList<ReconsolidationItem> cluster,
        ReconsolidationResponse response,
        HashSet<int> merged,
        List<string> problems)
    {
        var contradictions = new List<(ReconsolidationItem A, ReconsolidationItem B, string? Reason)>();
        foreach (var c in response.Contradictions.Where(c => c.A != c.B && c.A >= 1 && c.A <= cluster.Count && c.B >= 1 && c.B <= cluster.Count))
        {
            if (merged.Contains(c.A - 1) || merged.Contains(c.B - 1))
            {
                problems.Add($"sources {c.A} and {c.B} are both merged and contradicted");
                continue;
            }

            var (a, b) = (cluster[c.A - 1], cluster[c.B - 1]);
            if (!contradictions.Any(x => (x.A == a && x.B == b) || (x.A == b && x.B == a)))
            {
                contradictions.Add((a, b, c.Reason));
            }
        }

        return contradictions;
    }

    /// <summary>The canonical with every source fact and concrete name it lost put back, or null (with a problem
    /// recorded) when that would break the fact or body limit.</summary>
    private static MemoryCandidate? Restore(
        MemoryCandidate candidate, IReadOnlyList<ReconsolidationItem> sources, MemoryMergeOptions options, int number, List<string> problems)
    {
        var facts = candidate.Facts.ToList();
        var missingFacts = MergeVerifier.MissingFacts(sources.SelectMany(s => s.Facts), facts);
        if (facts.Count + missingFacts.Count > options.MaxFacts)
        {
            problems.Add($"canonical {number} cannot keep every source fact within the {options.MaxFacts}-fact limit");
            return null;
        }

        facts.AddRange(missingFacts);

        // ToCanonical unions every source's files into the canonical, so they already count as named.
        var files = candidate.FilesRead.Concat(candidate.FilesModified).Concat(sources.SelectMany(s =>
            EntityExtractor.ParseList(s.Memory.FilesReadJson).Concat(EntityExtractor.ParseList(s.Memory.FilesModifiedJson))));
        var missingNames = MergeVerifier.MissingNames(
            sources.SelectMany(s => s.Facts.Prepend(s.Memory.Body)),
            facts.Prepend(candidate.Body).Concat(files));
        var body = MergeVerifier.AppendNames(candidate.Body, missingNames, options.MaxBodyChars);
        if (body is null)
        {
            problems.Add($"canonical {number} cannot name every source identifier within the {options.MaxBodyChars}-character limit");
            return null;
        }

        return candidate with { Facts = facts, Body = body };
    }

    /// <summary>The canonical keeps every file its sources name; the model's topic wins, else the sources' most common one.</summary>
    private static ReconsolidationCanonical ToCanonical(MemoryCandidate candidate, IReadOnlyList<ReconsolidationItem> sources)
    {
        var modified = MemoryFields.UnionList(
            candidate.FilesModified, sources.SelectMany(s => EntityExtractor.ParseList(s.Memory.FilesModifiedJson)));
        var read = MemoryFields.UnionList(candidate.FilesRead, sources.SelectMany(s => EntityExtractor.ParseList(s.Memory.FilesReadJson)))
            .Where(f => !modified.Contains(f, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var topic = candidate.Topic ?? sources
            .Select(s => s.Memory.Topic)
            .OfType<string>()
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        return new ReconsolidationCanonical(
            candidate.Type,
            candidate.Title,
            candidate.Body,
            topic,
            candidate.Facts,
            candidate.Entities,
            read,
            modified,
            [.. sources.Select(s => s.Memory.Id)]);
    }
}
