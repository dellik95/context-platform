using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;
using Context.Providers;
using Context.Providers.Abstractions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using DomainMemory = Context.Domain.Entities.Memory;

namespace Context.Memory.Consolidation;

/// <summary>
/// Merges complementary memories of the same topic into a canonical memory while respecting
/// body/fact size limits and re-validating snapshots inside the write transaction.
/// </summary>
internal sealed class OnlineMergeExecutor(
    IMemoryStore store,
    IProviderResolver providerResolver,
    IUsageRecorder usageRecorder,
    ConsolidationOutcomeWriter outcomes,
    ILogger? logger)
{
    private const string MergedIntoNote = "merged into a broader memory";

    public async Task<ConsolidationResult> MergeAsync(
        IncomingMemory incoming,
        IReadOnlyList<ConsolidationNeighbour> complements,
        string? reason,
        MemoryMergeOptions options,
        IEmbeddingGenerator<string, Embedding<float>> generator,
        EmbeddingGenerationOptions? embeddingOptions,
        CancellationToken ct)
    {
        if (incoming.TopicKey is null
            || await outcomes.ReloadLiveAsync(complements[0].Memory.Id, ct).ConfigureAwait(false) is not { } snapshot
            || incoming.TopicKey != snapshot.TopicKey)
        {
            return await outcomes.InsertNewAsync(incoming, ct).ConfigureAwait(false);
        }

        var facts = FactMerger.Merge(EntityExtractor.ParseList(snapshot.FactsJson), incoming.Candidate.Facts);
        var bodyLength = snapshot.Body.Length + 2 + incoming.Candidate.Body.Length;
        if (facts.Count > options.MaxFacts || bodyLength > options.MaxBodyChars)
        {
            return await outcomes.InsertSiblingAsync(incoming, snapshot, ct).ConfigureAwait(false);
        }

        var extras = await CollectAdditionalSourcesAsync(incoming, snapshot, complements, facts, bodyLength, options, ct).ConfigureAwait(false);
        var finalFacts = extras.Facts;
        var body = await MergeBodiesAsync(incoming, snapshot, extras.Memories, finalFacts, options, ct).ConfigureAwait(false);
        var reembedding = await generator.GenerateAsync(MemoryEmbeddingText.For(snapshot.Title, body), embeddingOptions, ct).ConfigureAwait(false);
        var vector = new MemoryVector(incoming.Embedding!.ModelKey, reembedding.Vector.ToArray());

        return await store.InTransactionAsync(async () =>
        {
            if (await outcomes.ReloadLiveAsync(snapshot.Id, ct).ConfigureAwait(false) is not { } target || HasChanged(target, snapshot))
            {
                return await outcomes.InsertSiblingAsync(incoming, snapshot, ct).ConfigureAwait(false);
            }

            List<DomainMemory> sources = [];
            foreach (var extra in extras.Memories)
            {
                if (await outcomes.ReloadLiveAsync(extra.Id, ct).ConfigureAwait(false) is not { Status: MemoryStatus.Active } current || HasChanged(current, extra))
                {
                    return await outcomes.InsertSiblingAsync(incoming, snapshot, ct).ConfigureAwait(false);
                }

                sources.Add(current);
            }

            var mergedFacts = sources.Aggregate(
                FactMerger.Merge(EntityExtractor.ParseList(target.FactsJson), incoming.Candidate.Facts),
                (acc, source) => FactMerger.Merge(acc, EntityExtractor.ParseList(source.FactsJson)));
            if (mergedFacts.Count > options.MaxFacts)
            {
                return await outcomes.InsertSiblingAsync(incoming, snapshot, ct).ConfigureAwait(false);
            }

            return await CommitMergeAsync(incoming, target, sources, mergedFacts, body, vector, reason, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task<(List<DomainMemory> Memories, IReadOnlyList<string> Facts)> CollectAdditionalSourcesAsync(
        IncomingMemory incoming,
        DomainMemory snapshot,
        IReadOnlyList<ConsolidationNeighbour> complements,
        IReadOnlyList<string> facts,
        int bodyLength,
        MemoryMergeOptions options,
        CancellationToken ct)
    {
        List<DomainMemory> extras = [];
        foreach (var neighbour in complements.Skip(1))
        {
            if (extras.Count >= options.OnlineMaxSources - 2)
            {
                break;
            }

            if (await outcomes.ReloadLiveAsync(neighbour.Memory.Id, ct).ConfigureAwait(false) is not { Status: MemoryStatus.Active } extra
                || extra.TopicKey != incoming.TopicKey
                || extra.Type != snapshot.Type)
            {
                continue;
            }

            var withExtra = FactMerger.Merge(facts, EntityExtractor.ParseList(extra.FactsJson));
            var withExtraLength = bodyLength + 2 + extra.Body.Length;
            if (withExtra.Count <= options.MaxFacts && withExtraLength <= options.MaxBodyChars)
            {
                extras.Add(extra);
                facts = withExtra;
                bodyLength = withExtraLength;
            }
        }

        return (extras, facts);
    }

    private async Task<ConsolidationResult> CommitMergeAsync(
        IncomingMemory incoming,
        DomainMemory target,
        IReadOnlyList<DomainMemory> sources,
        IReadOnlyList<string> mergedFacts,
        string body,
        MemoryVector vector,
        string? reason,
        CancellationToken ct)
    {
        List<Guid> ids = [target.Id, .. sources.Select(m => m.Id)];
        var allEvidence = await store.ListEvidenceAsync(ids, ct).ConfigureAwait(false);
        var entityRows = await store.ListEntitiesAsync(ids, ct).ConfigureAwait(false);
        var targetEvidence = allEvidence.Where(e => e.MemoryId == target.Id).ToList();

        List<MemoryEvidence> added = [ConsolidationOutcomeWriter.NewEvidence(incoming, target.Id, EvidenceRelation.Merged, note: reason)];
        foreach (var source in sources)
        {
            added.AddRange(allEvidence.Where(e => e.MemoryId == source.Id).Select(e => CopyEvidence(e, target.Id, source.Id)));
            added.Add(ConsolidationOutcomeWriter.NewEvidence(incoming, target.Id, EvidenceRelation.Merged, source.Id, MergedIntoNote));
        }

        target.Body = body;
        ConsolidationOutcomeWriter.Absorb(target, incoming.Candidate, mergedFacts);
        foreach (var source in sources)
        {
            AbsorbMemory(target, source);
        }

        target.ReinforcementCount += 1 + sources.Sum(m => m.ReinforcementCount);
        target.LastReinforcedAt = incoming.Now;
        ConsolidationOutcomeWriter.AddCodeEvidence(added, targetEvidence, incoming, target.Id, incoming.CodeCheck);
        ConfidenceModel.Recompute(target, [.. targetEvidence, .. added]);

        await store.UpdateAsync(target, vector, MemoryFields.UnionEntities(target, entityRows, incoming.Entities), added, ct).ConfigureAwait(false);
        foreach (var source in sources)
        {
            await outcomes.SupersedeAndReleaseAsync(source, target.Id, incoming, MergedIntoNote, ct).ConfigureAwait(false);
        }

        return new ConsolidationResult(target, ConsolidationOutcome.Merged);
    }

    private async Task<string> MergeBodiesAsync(
        IncomingMemory incoming, DomainMemory target, IReadOnlyList<DomainMemory> extras, IReadOnlyList<string> facts, MemoryMergeOptions options, CancellationToken ct)
    {
        List<DomainMemory> memories = [target, .. extras];
        List<string> bodies = [.. memories.Select(m => m.Body), incoming.Candidate.Body];
        var fallback = MemoryMergePrompt.Fallback(bodies);
        var chatClient = await providerResolver.GetChatClientAsync(incoming.ProjectId, ct).ConfigureAwait(false);
        if (chatClient.IsNoOp())
        {
            return fallback;
        }

        List<RelationSubject> subjects = [.. memories.Select(RelationSubject.From), RelationSubject.From(incoming.Candidate)];
        List<ChatMessage> messages =
        [
            new(ChatRole.System, MemoryMergePrompt.SystemPrompt),
            new(ChatRole.User, MemoryMergePrompt.BuildUserPrompt(subjects, facts, options.MaxBodyChars)),
        ];

        try
        {
            var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct).ConfigureAwait(false);
            await usageRecorder.RecordChatUsageAsync(incoming.ProjectId, incoming.SessionId, "MemoryMerge", chatClient, response.Usage, logger, ct).ConfigureAwait(false);
            return AcceptMergedBody(MemoryMergePrompt.Clean(response.Text), bodies, facts, options) ?? fallback;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(ex, "LLM provider {Provider} failed to merge memory {MemoryId}; concatenating bodies.", chatClient.GetProviderName(), target.Id);
            return fallback;
        }
    }

    private string? AcceptMergedBody(string? merged, IReadOnlyList<string> sourceBodies, IReadOnlyList<string> facts, MemoryMergeOptions options)
    {
        if (merged is null || merged.Length > options.MaxBodyChars)
        {
            return null;
        }

        var missing = MergeVerifier.MissingNames([.. sourceBodies, .. facts], [merged, .. facts]);
        if (missing.Count > 0)
        {
            logger?.LogInformation("Merged memory body dropped {Count} concrete names ({Names}); concatenating bodies instead.", missing.Count, string.Join(", ", missing.Take(5)));
            return null;
        }

        return merged;
    }

    private static bool HasChanged(DomainMemory current, DomainMemory snapshot) =>
        current.Title != snapshot.Title || current.Body != snapshot.Body || current.TopicKey != snapshot.TopicKey;

    private static void AbsorbMemory(DomainMemory memory, DomainMemory other)
    {
        memory.ConceptsJson = MemoryFields.UnionJson(memory.ConceptsJson, EntityExtractor.ParseList(other.ConceptsJson));
        memory.FilesReadJson = MemoryFields.UnionJson(memory.FilesReadJson, EntityExtractor.ParseList(other.FilesReadJson));
        memory.FilesModifiedJson = MemoryFields.UnionJson(memory.FilesModifiedJson, EntityExtractor.ParseList(other.FilesModifiedJson));
        memory.ScopePath ??= other.ScopePath;
        memory.Tier = (MemoryTier)Math.Max((int)memory.Tier, (int)other.Tier);
        memory.Pinned |= other.Pinned;
    }

    private static MemoryEvidence CopyEvidence(MemoryEvidence evidence, Guid memoryId, Guid fromMemoryId) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = evidence.ProjectId,
        MemoryId = memoryId,
        Relation = evidence.Relation,
        Source = evidence.Source,
        SessionId = evidence.SessionId,
        SourceMemoryId = evidence.SourceMemoryId ?? fromMemoryId,
        ObservedAt = evidence.ObservedAt,
        Note = evidence.Note,
    };
}
