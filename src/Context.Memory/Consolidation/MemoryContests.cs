using Context.Domain.Entities;
using Context.Domain.Enums;
using Context.Memory.Abstractions;

namespace Context.Memory.Consolidation;

/// <summary>Contest bookkeeping shared by consolidation, reconsolidation and decay: a memory that leaves
/// the live set hands its contest partners back to Active once nothing live contests them.</summary>
public static class MemoryContests
{
    /// <summary>Still-contested memories <paramref name="memoryId"/> was recorded as contradicting.</summary>
    public static async Task<IReadOnlyList<Context.Domain.Entities.Memory>> PartnersAsync(IMemoryStore store, Guid memoryId, CancellationToken ct)
    {
        var evidence = await store.ListEvidenceAsync([memoryId], ct).ConfigureAwait(false);
        var ids = evidence
            .Where(e => e.Relation == EvidenceRelation.Contradicted && e.SourceMemoryId is not null)
            .Select(e => e.SourceMemoryId!.Value)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return [.. (await store.GetByIdsAsync(ids, ct).ConfigureAwait(false)).Where(m => m.Status == MemoryStatus.Contested)];
    }

    /// <summary>Supersedes <paramref name="loser"/> by <paramref name="winnerId"/>, records
    /// <paramref name="supersededEvidence"/> on it, and releases its former contest partners.</summary>
    public static async Task SupersedeAndReleaseAsync(
        IMemoryStore store,
        Context.Domain.Entities.Memory loser,
        Guid winnerId,
        DateTimeOffset at,
        MemoryEvidence supersededEvidence,
        CancellationToken ct)
    {
        if (!await store.SupersedeAsync(loser.Id, winnerId, at, ct).ConfigureAwait(false))
        {
            return;
        }

        await store.AddEvidenceAsync([supersededEvidence], ct).ConfigureAwait(false);
        await ReleasePartnersAsync(store, loser.Id, winnerId, ct).ConfigureAwait(false);
    }

    /// <summary>Marks <paramref name="memoryId"/> Obsolete and releases its contest partners in one store
    /// transaction; false when it was no longer live.</summary>
    public static Task<bool> RetireAsync(IMemoryStore store, Guid memoryId, CancellationToken ct) =>
        store.InTransactionAsync(async () =>
        {
            if (!await store.ObsoleteAsync(memoryId, ct).ConfigureAwait(false))
            {
                return false;
            }

            await ReleasePartnersAsync(store, memoryId, null, ct).ConfigureAwait(false);
            return true;
        }, ct);

    private static async Task ReleasePartnersAsync(IMemoryStore store, Guid retiredId, Guid? winnerId, CancellationToken ct)
    {
        foreach (var partner in await PartnersAsync(store, retiredId, ct).ConfigureAwait(false))
        {
            if (partner.Id == winnerId || (await PartnersAsync(store, partner.Id, ct).ConfigureAwait(false)).Count > 0)
            {
                continue;
            }

            partner.Status = MemoryStatus.Active;
            ConfidenceModel.Recompute(partner, await store.ListEvidenceAsync([partner.Id], ct).ConfigureAwait(false));
            await store.UpdateAsync(partner, null, null, [], ct).ConfigureAwait(false);
        }
    }
}
