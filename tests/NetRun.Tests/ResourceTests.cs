using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>RES-001 and §10: atomic accounting, isolated pools, leases, no double credit.</summary>
internal static class ResourceTests
{
    public static void Run()
    {
        Section("virtual resources");
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var host = runtime.RegisterHost(new HostDefinition("server.1", HostClass.Server,
            Compute: new ComputeComponent(4, 2), Memory: new MemoryComponent(4),
            Storage: new StorageComponent(1_000))).Host!;
        var owner = runtime.NewPrincipal();

        var initial = runtime.Resources.Snapshot(host.Id)!;
        Equal(4, initial.CapacityCu, "capacity comes from the component");
        Equal(4_000L, initial.BufferMilliCu, "a fresh buffer starts full");
        Equal(2, initial.RateCuPerSecond, "the sustained rate comes from the component");

        // Two consumers compete for the same finite capacity.
        var first = runtime.Resources.ReserveCompute(host.Id, owner, 3, 10_000);
        Equal(ReservationStatus.Ok, first.Status, "the first reservation fits");
        var second = runtime.Resources.ReserveCompute(host.Id, owner, 2, 10_000);
        Equal(ReservationStatus.InsufficientCompute, second.Status, "the second reservation does not fit");
        Equal(1_000L, runtime.Resources.Snapshot(host.Id)!.BufferMilliCu, "only the granted amount left the buffer");

        var released = runtime.Resources.Release(first.Reservation!.Id, owner);
        Equal(ReservationStatus.Ok, released, "the holder releases its reservation");
        Equal(4_000L, runtime.Resources.Snapshot(host.Id)!.BufferMilliCu, "the buffer is restored on release");

        // A retried release must never credit twice.
        var replay = runtime.Resources.Release(first.Reservation.Id, owner);
        Equal(ReservationStatus.Duplicate, replay, "a repeated release is a duplicate");
        Equal(4_000L, runtime.Resources.Snapshot(host.Id)!.BufferMilliCu, "a repeated release credits nothing");

        // Memory is a separate pool from compute.
        var slots = runtime.Resources.ReserveMemory(host.Id, owner, 4, 10_000);
        Equal(ReservationStatus.Ok, slots.Status, "all memory slots may be taken");
        Equal(ReservationStatus.InsufficientMemory, runtime.Resources.ReserveMemory(host.Id, owner, 1, 10_000).Status,
            "memory is exhausted independently of compute");
        Equal(4_000L, runtime.Resources.Snapshot(host.Id)!.BufferMilliCu, "taking memory does not spend compute");
        runtime.Resources.Release(slots.Reservation!.Id, owner);

        // Recovery is credited once per elapsed interval, never more.
        runtime.Resources.ReserveCompute(host.Id, owner, 4, 10_000);
        Equal(0L, runtime.Resources.Snapshot(host.Id)!.BufferMilliCu, "the buffer is empty");
        runtime.Resources.AdvanceTo(clock.NowMilliseconds + 1_000);
        Equal(2_000L, runtime.Resources.Snapshot(host.Id)!.BufferMilliCu, "one second at 2 CU/s credits 2 CU");
        runtime.Resources.AdvanceTo(clock.NowMilliseconds + 1_000);
        clock.Advance(1_000);
        runtime.Resources.AdvanceTo(clock.NowMilliseconds);
        Equal(2_000L, runtime.Resources.Snapshot(host.Id)!.BufferMilliCu, "the same interval is never credited twice");

        // Recovery is bounded by capacity.
        for (var i = 0; i < 20; i++)
        {
            clock.Advance(1_000);
            runtime.Resources.AdvanceTo(clock.NowMilliseconds);
        }
        Equal(4_000L, runtime.Resources.Snapshot(host.Id)!.BufferMilliCu, "recovery never exceeds capacity");

        // Leases expire on game time and return their capacity once.
        var lease = runtime.Resources.ReserveCompute(host.Id, owner, 2, 500);
        Equal(ReservationStatus.Ok, lease.Status, "a lease is granted");
        clock.Advance(600);
        runtime.Resources.AdvanceTo(clock.NowMilliseconds);
        Equal(ReservationState.Expired, runtime.Resources.Get(lease.Reservation!.Id)!.State, "the lease expired");
        Equal(ReservationStatus.Duplicate, runtime.Resources.Release(lease.Reservation.Id, owner),
            "releasing an expired lease credits nothing");
        Equal(4_000L, runtime.Resources.Snapshot(host.Id)!.BufferMilliCu, "the expired lease returned its capacity");

        // Lease confirmation extends without re-reserving.
        var renewable = runtime.Resources.ReserveCompute(host.Id, owner, 1, 500);
        clock.Advance(400);
        Equal(ReservationStatus.Ok, runtime.Resources.Confirm(renewable.Reservation!.Id, owner, 5_000),
            "a lease may be confirmed");
        clock.Advance(400);
        runtime.Resources.AdvanceTo(clock.NowMilliseconds);
        Equal(ReservationState.Active, runtime.Resources.Get(renewable.Reservation.Id)!.State,
            "a confirmed lease outlives its original term");

        // Another principal cannot release somebody else's lease.
        var other = runtime.NewPrincipal();
        Equal(ReservationStatus.StalePrincipal, runtime.Resources.Release(renewable.Reservation.Id, other),
            "a lease belongs to its owner");

        // Destroying the host revokes everything that depended on it.
        var doomed = runtime.RegisterHost(new HostDefinition("server.2", HostClass.Server,
            Compute: new ComputeComponent(2, 1))).Host!;
        var doomedLease = runtime.Resources.ReserveCompute(doomed.Id, owner, 1, 60_000);
        Equal(ReservationStatus.Ok, doomedLease.Status, "a lease on the doomed host is granted");
        runtime.DestroyHost(doomed.Id, doomed.Generation);
        Equal(ReservationState.Revoked, runtime.Resources.Get(doomedLease.Reservation!.Id)!.State,
            "destroying a host revokes its leases");
        Check(runtime.Resources.Snapshot(doomed.Id) is null, "a destroyed host has no pools");

        // A reservation against an unknown host never silently succeeds.
        Equal(ReservationStatus.UnknownHost, runtime.Resources.ReserveCompute(new HostId(999), owner, 1, 1_000).Status,
            "an unknown provider refuses the reservation");

        // Opening more than the provider has is refused, not clamped.
        Equal(ReservationStatus.InsufficientStorage,
            runtime.Resources.ReserveStorage(host.Id, owner, 2_000, 1_000).Status,
            "storage reservations respect the component size");
    }
}
