using NetDM.Core;
using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>HOST-001: idempotent registration, generations, lifecycle and cascade on destruction.</summary>
internal static class HostLifecycleTests
{
    public static void Run()
    {
        Section("host lifecycle");
        var clock = new ManualSimulationClock();
        var hosts = new HostRegistry(clock);
        var definition = new HostDefinition("apc.1", HostClass.AccessPoint,
            Network: new NetworkComponent(new NodeId(7), "station", Wired: true, RadioRange: 6, RadioChannel: 1),
            Compute: new ComputeComponent(3, 2),
            Memory: new MemoryComponent(4));

        var first = hosts.Register(definition);
        Check(first.Succeeded, "a new host is registered");
        Equal(HostStatus.Ok, first.Status, "first registration is not a duplicate");
        Check(first.Host is not null && first.Host.Generation.Value == 1, "a new host starts at generation 1");
        Check(first.Host!.Available, "a freshly registered host is powered and operational");

        var again = hosts.Register(definition);
        Equal(HostStatus.Duplicate, again.Status, "the same declaration under the same name is idempotent");
        Equal(first.Host.Id, again.Host!.Id, "a duplicate registration returns the live host");

        var conflicting = hosts.Register(definition with { Compute = new ComputeComponent(20, 8) });
        Equal(HostStatus.Conflict, conflicting.Status, "a different declaration under a live name is a conflict");
        Equal(3, hosts.Get("apc.1")!.Compute!.CapacityCu, "a conflict leaves the live host untouched");

        var staleGeneration = hosts.SetLifecycle(first.Host.Id, new HostGeneration(9), false, false);
        Equal(HostStatus.StaleGeneration, staleGeneration.Status, "a stale generation cannot change lifecycle");
        Check(hosts.Get(first.Host.Id)!.Available, "the live host is unchanged after a stale lifecycle write");

        var powered = hosts.SetLifecycle(first.Host.Id, first.Host.Generation, powered: false, operational: true);
        Equal(HostStatus.Ok, powered.Status, "the current generation may change lifecycle");
        Check(!hosts.Get(first.Host.Id)!.Available, "an unpowered host is not available");

        var retired = hosts.Destroy(first.Host.Id, first.Host.Generation);
        Equal(HostStatus.Ok, retired.Status, "destruction with the current generation succeeds");
        Check(hosts.Get(first.Host.Id) is null, "a destroyed host is gone");
        Check(hosts.IsRetired("apc.1"), "a destroyed name is remembered as retired");
        Equal(HostStatus.UnknownHost, hosts.Destroy(first.Host.Id, first.Host.Generation).Status,
            "destroying twice does not create or revive anything");

        var reborn = hosts.Register(definition);
        Equal(HostStatus.Ok, reborn.Status, "a retired name may be registered again as a new instance");
        var rebound = hosts.Rebind("apc.1", definition with { Compute = new ComputeComponent(5, 3) });
        Equal(HostStatus.Ok, rebound.Status, "rebinding a live name succeeds");
        Equal(2UL, rebound.Host!.Generation.Value, "rebinding increases the generation");
        Equal(HostStatus.StaleGeneration,
            hosts.SetLifecycle(reborn.Host!.Id, reborn.Host.Generation, false, false).Status,
            "the previous generation is fenced after a rebind");

        // A host without a component simply does not have that ability.
        var terminal = hosts.Register(new HostDefinition("terminal.1", HostClass.Terminal,
            Network: new NetworkComponent(new NodeId(8), "station", Wired: false, RadioRange: 4, RadioChannel: 1)));
        Check(terminal.Host!.Compute is null, "a terminal without a compute component has no compute");
        Check(terminal.Host.Security is null, "a host without a security component uses the standard profile");

        Equal(HostStatus.InvalidRequest, hosts.Register(new HostDefinition("", HostClass.Server)).Status,
            "an empty name is refused");
        Equal(HostStatus.InvalidRequest, hosts.Register(new HostDefinition("bad name", HostClass.Server)).Status,
            "a name with a space is refused");
    }
}
