using Context.Memory.Abstractions;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Context.Memory.Consolidation;

/// <summary>
/// Compares an incoming candidate memory against its discovered neighbours using either the
/// project's chat model or deterministic offline heuristics.
/// </summary>
internal sealed class CandidateRelationAssessor(
    IProviderResolver providerResolver,
    IUsageRecorder usageRecorder,
    ILogger? logger)
{
    private static readonly MemoryRelation[] VerdictPriority =
        [MemoryRelation.Duplicate, MemoryRelation.Supersedes, MemoryRelation.Contradicts, MemoryRelation.Complements];

    public async Task<(MemoryRelation Relation, ConsolidationNeighbour? Target, string? Reason, IReadOnlyList<ConsolidationNeighbour> Complements)> JudgeAsync(
        IncomingMemory incoming, IReadOnlyList<ConsolidationNeighbour> neighbours, CancellationToken ct)
    {
        var candidate = RelationSubject.From(incoming.Candidate);
        var subjects = neighbours.Select(n => RelationSubject.From(n.Memory)).ToList();
        var negation = subjects.Select(s => MemoryRelationJudge.HasNegationAsymmetry(candidate, s)).ToList();

        for (var i = 0; i < neighbours.Count; i++)
        {
            if (neighbours[i].Cosine >= MemoryConsolidator.FastDuplicateCosine
                && neighbours[i].Memory.Type == incoming.Candidate.Type
                && !negation[i])
            {
                return (MemoryRelation.Duplicate, neighbours[i], "near-identical embedding", []);
            }
        }

        var verdicts = await JudgeWithModelAsync(incoming, candidate, subjects, ct).ConfigureAwait(false)
            ?? JudgeOffline(incoming, neighbours, candidate, subjects, negation);

        foreach (var relation in VerdictPriority)
        {
            for (var i = 0; i < neighbours.Count; i++)
            {
                if (verdicts[i].Relation == relation)
                {
                    IReadOnlyList<ConsolidationNeighbour> complements = relation == MemoryRelation.Complements
                        ? [.. neighbours.Where((_, j) => verdicts[j].Relation == MemoryRelation.Complements)]
                        : [];
                    return (relation, neighbours[i], verdicts[i].Reason, complements);
                }
            }
        }

        return (MemoryRelation.Unrelated, null, null, []);
    }

    private async Task<IReadOnlyList<RelationVerdict>?> JudgeWithModelAsync(
        IncomingMemory incoming, RelationSubject candidate, IReadOnlyList<RelationSubject> subjects, CancellationToken ct)
    {
        var chatClient = await providerResolver.GetChatClientAsync(incoming.ProjectId, ct).ConfigureAwait(false);
        if (chatClient.IsNoOp())
        {
            return null;
        }

        List<ChatMessage> messages =
        [
            new(ChatRole.System, MemoryRelationJudge.SystemPrompt),
            new(ChatRole.User, MemoryRelationJudge.BuildUserPrompt(candidate, subjects)),
        ];

        try
        {
            var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            await usageRecorder.RecordChatUsageAsync(incoming.ProjectId, incoming.SessionId, "MemoryRelationJudge", chatClient, response.Usage, logger, ct).ConfigureAwait(false);
            return MemoryRelationJudge.Parse(response.Text, subjects.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "LLM provider {Provider} failed to judge memory relations for project {ProjectId}; using the offline heuristic.",
                chatClient.GetProviderName(), incoming.ProjectId);
            return null;
        }
    }

    private static IReadOnlyList<RelationVerdict> JudgeOffline(
        IncomingMemory incoming,
        IReadOnlyList<ConsolidationNeighbour> neighbours,
        RelationSubject candidate,
        IReadOnlyList<RelationSubject> subjects,
        IReadOnlyList<bool> negation)
    {
        var candidateStrong = EntityExtractor.StrongKeys(incoming.Entities.Select(e => e.Key));
        return [.. neighbours.Select((n, i) =>
        {
            var sharedCount = n.StrongKeys.Count(candidateStrong.Contains);
            var jaccard = MemoryRelationJudge.ComputeContentJaccard(candidate, subjects[i]);
            return new RelationVerdict(
                MemoryRelationJudge.JudgeOffline(new RelationSignals(
                    n.Cosine,
                    sharedCount > 0,
                    n.Memory.Type == incoming.Candidate.Type,
                    incoming.TopicKey is not null && incoming.TopicKey == n.Memory.TopicKey,
                    negation[i],
                    jaccard,
                    sharedCount)),
                "offline heuristic");
        })];
    }
}
