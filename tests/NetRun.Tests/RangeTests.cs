using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// §8.2 and PRIM-003: an action reaches only as far as it declares. Reach is decided by the transport, not by
/// the action, and an unreachable target is a refusal rather than a hopeful attempt.
/// </summary>
internal static class RangeTests
{
    /// <summary>A fixed map of distances, standing in for the transport.</summary>
    private sealed class Hops(params (ulong From, ulong To, int Hops)[] edges) : IHopDistance
    {
        public int Distance(HostId from, HostId to)
        {
            if (from == to)
                return 0;
            foreach (var (a, b, hops) in edges)
                if ((a == from.Value && b == to.Value) || (b == from.Value && a == to.Value))
                    return hops;
            return -1;
        }
    }

    public static void Run()
    {
        Section("range");
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.1", HostClass.Cyberdeck,
            Compute: new ComputeComponent(50, 10), Memory: new MemoryComponent(4))).Host!;
        var near = runtime.RegisterHost(new HostDefinition("near.1", HostClass.Server,
            Compute: new ComputeComponent(10, 2), Memory: new MemoryComponent(2))).Host!;
        var far = runtime.RegisterHost(new HostDefinition("far.1", HostClass.Server,
            Compute: new ComputeComponent(10, 2), Memory: new MemoryComponent(2))).Host!;
        var lost = runtime.RegisterHost(new HostDefinition("lost.1", HostClass.Server,
            Compute: new ComputeComponent(10, 2), Memory: new MemoryComponent(2))).Host!;
        var actor = runtime.NewPrincipal();
        foreach (var host in new[] { deck.Id, near.Id, far.Id, lost.Id })
        {
            runtime.Authority.Grant(actor, host, CapabilityCategory.Execution, null, 600_000, GrantSource.Granted, PrincipalId.None);
            runtime.Authority.Grant(actor, host, CapabilityCategory.Read, null, 600_000, GrantSource.Granted, PrincipalId.None);
        }
        // Perception needs knowledge of the target, which the test states directly.
        foreach (var host in new[] { near.Id, far.Id, lost.Id })
            runtime.Knowledge.Remember(actor, host, KnowledgeLevel.Observed, 90);
        var inspect = runtime.Actions.GetAction("net.inspect")!;

        // Without a distance source the pipeline cannot judge reach: the action runs.
        Equal(ActionStatus.Committed, Execute(runtime, actor, deck.Id, far.Id, "r-nosource", inspect).Status,
            "without a transport the declared range is not judged");

        runtime.Actions.AttachRange(new Hops((deck.Id.Value, near.Id.Value, 3), (deck.Id.Value, far.Id.Value, 12)));
        Equal(ActionStatus.Committed, Execute(runtime, actor, deck.Id, deck.Id, "r-self", inspect).Status,
            "the host itself is always in reach");
        Equal(ActionStatus.Committed, Execute(runtime, actor, deck.Id, near.Id, "r-near", inspect).Status,
            "a target inside the declared reach is allowed");
        var tooFar = Execute(runtime, actor, deck.Id, far.Id, "r-far", inspect);
        Equal(ActionStatus.OutOfRange, tooFar.Status, "a target beyond the declared reach is refused");
        Equal("range", tooFar.Reason, "the refusal names reach");
        var unreachable = Execute(runtime, actor, deck.Id, lost.Id, "r-lost", inspect);
        Equal(ActionStatus.OutOfRange, unreachable.Status, "an unreachable target is refused");
        Equal("no_route", unreachable.Reason, "the refusal names the missing route");

        // Reach is checked before anything is spent: a refused attempt costs no compute.
        var before = runtime.Resources.Snapshot(deck.Id)!.BufferMilliCu;
        Execute(runtime, actor, deck.Id, far.Id, "r-far-2", inspect);
        Equal(before, runtime.Resources.Snapshot(deck.Id)!.BufferMilliCu,
            "a refused attempt spends nothing");

        // The range comes from the action, so two actions on the same target can differ.
        var scan = runtime.Actions.GetAction("net.scan")!;
        Equal(12, scan.Descriptor.RangeHops, "the scan declares a longer reach than the inspection");
    }

    private static ActionOutcome Execute(NetRunRuntime runtime, PrincipalId actor, HostId execution, HostId target,
        string key, IGameAction action) =>
        runtime.Actions.Execute(new ActionRequest(actor, execution, target, EntityId.None, ProcessId.None,
            CorrelationId.None, key), action);
}
