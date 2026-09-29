namespace Context.Application.Jobs;

/// <summary>
/// Decides how many jobs each <see cref="JobDispatcher"/> lane (one per project, plus the host-wide
/// one) runs at once. Every lane owns <c>reservedPerLane</c> slots nobody else can take, so a project
/// whose handlers are slow or stuck never starves another. On top of that, <c>shared</c> host-wide
/// slots let a lane with a backlog burst while the others sit idle, split evenly across every lane
/// competing for them. There is no preemption: a lane over its share when a new contender shows up
/// hands the excess back as its running jobs finish, and each freed shared slot goes to the neediest
/// waiting lane rather than straight back to its previous holder.
/// </summary>
internal sealed class JobWorkerPool(int reservedPerLane, int shared)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, LaneUsage> _usage = [];
    private int _sharedInUse;
    private long _waitSequence;

    /// <summary>False when the lane's reserved slots are full and it can't borrow a shared one; the
    /// lane is then remembered as waiting and handed back by a <see cref="Release"/> that frees one.</summary>
    public bool TryAcquire(Guid lane, out bool borrowed)
    {
        lock (_gate)
        {
            if (!_usage.TryGetValue(lane, out var usage))
            {
                _usage[lane] = usage = new LaneUsage();
            }

            borrowed = false;
            if (usage.Reserved < reservedPerLane)
            {
                usage.Reserved++;
                return true;
            }

            if (_sharedInUse < shared && usage.Borrowed < FairShare(usage))
            {
                usage.Borrowed++;
                _sharedInUse++;
                borrowed = true;
                return true;
            }

            usage.WaitingSince ??= ++_waitSequence;
            return false;
        }
    }

    /// <summary>
    /// Returns the waiting lanes to wake, fewest borrowed slots first, then longest waiting — only
    /// ever non-empty when a shared slot was freed. <paramref name="idle"/> means the lane found
    /// nothing runnable, so it stops counting against the others' fair share.
    /// </summary>
    public IReadOnlyList<Guid> Release(Guid lane, bool borrowed, bool idle)
    {
        lock (_gate)
        {
            var usage = _usage[lane];
            if (borrowed)
            {
                usage.Borrowed--;
                _sharedInUse--;
            }
            else
            {
                usage.Reserved--;
            }

            if (idle)
            {
                usage.WaitingSince = null;
            }

            if (usage is { Reserved: 0, Borrowed: 0, WaitingSince: null })
            {
                _usage.Remove(lane);
            }

            if (!borrowed)
            {
                return [];
            }

            return _usage
                .Where(entry => entry.Key != lane && entry.Value.WaitingSince is not null)
                .OrderBy(entry => entry.Value.Borrowed)
                .ThenBy(entry => entry.Value.WaitingSince)
                .Select(entry => entry.Key)
                .ToList();
        }
    }

    private int FairShare(LaneUsage self)
    {
        var contenders = 1;
        foreach (var other in _usage.Values)
        {
            if (other != self && (other.Borrowed > 0 || other.WaitingSince is not null))
            {
                contenders++;
            }
        }

        return (shared + contenders - 1) / contenders;
    }

    private sealed class LaneUsage
    {
        public int Reserved;
        public int Borrowed;
        public long? WaitingSince;
    }
}
