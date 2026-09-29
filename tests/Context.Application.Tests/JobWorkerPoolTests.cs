using Context.Application.Jobs;
using FluentAssertions;

namespace Context.Application.Tests;

public sealed class JobWorkerPoolTests
{
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();
    private readonly Guid _c = Guid.NewGuid();

    [Fact]
    public void A_lone_busy_lane_bursts_into_every_shared_slot()
    {
        var pool = new JobWorkerPool(reservedPerLane: 2, shared: 4);

        AcquireMany(pool, _a, 6).Should().OnlyContain(acquired => acquired);
        pool.TryAcquire(_a, out _).Should().BeFalse();
    }

    [Fact]
    public void Reserved_slots_stay_available_when_another_lane_holds_the_whole_shared_pool()
    {
        var pool = new JobWorkerPool(reservedPerLane: 2, shared: 2);
        AcquireMany(pool, _a, 4);

        pool.TryAcquire(_b, out var first).Should().BeTrue();
        pool.TryAcquire(_b, out var second).Should().BeTrue();
        (first || second).Should().BeFalse("both come from B's own reserved slots");
        pool.TryAcquire(_b, out _).Should().BeFalse();
    }

    [Fact]
    public void A_lane_over_its_share_hands_freed_shared_slots_to_a_waiting_lane()
    {
        var pool = new JobWorkerPool(reservedPerLane: 1, shared: 4);
        AcquireMany(pool, _a, 5);
        pool.TryAcquire(_b, out _).Should().BeTrue();
        pool.TryAcquire(_b, out _).Should().BeFalse();

        for (var i = 0; i < 2; i++)
        {
            pool.Release(_a, borrowed: true, idle: false).Should().Equal(_b);
            pool.TryAcquire(_a, out _).Should().BeFalse("A already holds more than half the shared slots");
            pool.TryAcquire(_b, out var borrowed).Should().BeTrue();
            borrowed.Should().BeTrue();
        }

        pool.Release(_a, borrowed: true, idle: false);
        pool.TryAcquire(_b, out _).Should().BeFalse("B is at its even share now");
        pool.TryAcquire(_a, out _).Should().BeTrue();
    }

    [Fact]
    public void A_lane_that_goes_idle_stops_counting_against_the_fair_share()
    {
        var pool = new JobWorkerPool(reservedPerLane: 1, shared: 4);
        AcquireMany(pool, _a, 5);
        pool.TryAcquire(_b, out _).Should().BeTrue();
        pool.TryAcquire(_b, out _).Should().BeFalse();

        pool.Release(_b, borrowed: false, idle: true);

        pool.Release(_a, borrowed: true, idle: false).Should().BeEmpty();
        pool.TryAcquire(_a, out var borrowed).Should().BeTrue();
        borrowed.Should().BeTrue();
    }

    [Fact]
    public void Freed_shared_slots_wake_the_neediest_waiting_lane_first()
    {
        var pool = new JobWorkerPool(reservedPerLane: 1, shared: 3);
        AcquireMany(pool, _a, 2);
        AcquireMany(pool, _c, 3);
        pool.TryAcquire(_c, out _).Should().BeFalse();
        pool.TryAcquire(_b, out _).Should().BeTrue();
        pool.TryAcquire(_b, out _).Should().BeFalse();

        pool.Release(_a, borrowed: true, idle: false).Should().Equal(_b, _c);
    }

    [Fact]
    public void Releasing_a_reserved_slot_wakes_nobody()
    {
        var pool = new JobWorkerPool(reservedPerLane: 1, shared: 0);
        pool.TryAcquire(_a, out _).Should().BeTrue();
        pool.TryAcquire(_b, out _).Should().BeTrue();
        pool.TryAcquire(_b, out _).Should().BeFalse();

        pool.Release(_a, borrowed: false, idle: false).Should().BeEmpty();
    }

    private static List<bool> AcquireMany(JobWorkerPool pool, Guid lane, int count) =>
        Enumerable.Range(0, count).Select(_ => pool.TryAcquire(lane, out var _)).ToList();
}
