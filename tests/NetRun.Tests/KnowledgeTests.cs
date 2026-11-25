using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>§9.4 and PRIM-002: an observer learns by acting, knowledge ages, and nothing is revealed for free.</summary>
internal static class KnowledgeTests
{
    public static void Run()
    {
        Section("knowledge");
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.1", HostClass.Cyberdeck,
            Compute: new ComputeComponent(50, 10), Memory: new MemoryComponent(4))).Host!;
        var relay = runtime.RegisterHost(new HostDefinition("relay.1", HostClass.Relay,
            Compute: new ComputeComponent(4, 1))).Host!;
        var hidden = runtime.RegisterHost(new HostDefinition("hidden.1", HostClass.Server,
            Compute: new ComputeComponent(4, 1),
            Security: new SecurityComponent(SecurityProfile.Secure, 2, 1))).Host!;
        var actor = runtime.NewPrincipal();

        // Nothing is known before anything is done.
        Equal(KnowledgeLevel.Unknown, runtime.Knowledge.Level(actor, relay.Id), "a new observer knows nothing");
        Check(!runtime.Knowledge.Holds(actor, relay.Id, KnowledgeLevel.Observed), "and cannot act on what it does not know");

        runtime.Authority.Grant(actor, deck.Id, CapabilityCategory.Execution, null, 120_000, GrantSource.Granted, PrincipalId.None);
        runtime.Authority.Grant(actor, deck.Id, CapabilityCategory.Discovery, null, 120_000, GrantSource.Granted, PrincipalId.None);
        runtime.Authority.Grant(actor, deck.Id, CapabilityCategory.Read, null, 120_000, GrantSource.Granted, PrincipalId.None);
        runtime.Authority.Grant(actor, relay.Id, CapabilityCategory.Read, null, 120_000, GrantSource.Granted, PrincipalId.None);

        // Inspecting your own host needs no prior knowledge; inspecting another one does.
        var inspect = runtime.Actions.GetAction("net.inspect")!;
        Equal(ActionStatus.Committed, Execute(runtime, actor, deck.Id, deck.Id, "k-own", inspect).Status,
            "a principal may inspect the host it runs from");
        Equal(ActionStatus.InvalidState, Execute(runtime, actor, deck.Id, relay.Id, "k-blind", inspect).Status,
            "inspecting an unknown host is refused");
        // Probing for objects that are not there answers exactly like probing without a grant: the refusal
        // never tells an observer whether an identifier belongs to something.
        var missingA = runtime.Actions.Execute(Request(actor, deck.Id, new HostId(9_999), "k-missing-a"), inspect);
        var missingB = runtime.Actions.Execute(Request(actor, deck.Id, new HostId(8_888), "k-missing-b"), inspect);
        Equal(ActionStatus.Forbidden, missingA.Status, "a target with no grant is refused");
        Equal(missingA.Status, missingB.Status, "two different unknown identifiers answer identically");

        // Scanning is how knowledge begins, and only for hosts the observer may discover.
        runtime.Authority.Grant(actor, relay.Id, CapabilityCategory.Discovery, null, 120_000, GrantSource.Granted, PrincipalId.None);
        var scan = runtime.Actions.GetAction("net.scan")!;
        var scanned = Execute(runtime, actor, deck.Id, deck.Id, "k-scan", scan);
        Equal(ActionStatus.Committed, scanned.Status, "the scan runs");
        Equal(KnowledgeLevel.Observed, runtime.Knowledge.Level(actor, relay.Id), "the scan observed the relay");
        Check(!scanned.Result!.Contains("hidden", StringComparison.Ordinal),
            "a host the observer may not discover is not in the answer");

        var known = Execute(runtime, actor, deck.Id, relay.Id, "k-known", inspect);
        Equal(ActionStatus.Committed, known.Status, "a host the observer knows may be inspected");
        Check(known.Result!.Contains("relay.1", StringComparison.Ordinal), "the answer carries the state of the host");

        // Knowledge ages: it stays remembered but is no longer fresh enough to act on.
        clock.Advance(runtime.Knowledge.StaleAfterMilliseconds + 1);
        var record = runtime.Knowledge.Lookup(actor, relay.Id)!;
        Check(runtime.Knowledge.IsStale(record), "an old record is marked stale");
        Equal(KnowledgeLevel.Observed, record.Level, "a stale record still remembers what was learned");
        Check(!runtime.Knowledge.Holds(actor, relay.Id, KnowledgeLevel.Observed), "stale knowledge does not authorize an action");

        // Re-confirming makes it fresh again without raising the level.
        var rescanned = Execute(runtime, actor, deck.Id, deck.Id, "k-scan-2", scan);
        Equal(ActionStatus.Committed, rescanned.Status, "the scan can be repeated after its cooldown");
        Check(!runtime.Knowledge.IsStale(runtime.Knowledge.Lookup(actor, relay.Id)!), "a new scan makes the record fresh");

        // Knowledge disappears with the object it describes.
        runtime.DestroyHost(relay.Id, relay.Generation);
        Equal(KnowledgeLevel.Unknown, runtime.Knowledge.Level(actor, relay.Id), "a destroyed host is forgotten");

        // And it disappears after the forget window even for a live object.
        clock.Advance(2_100);
        var scanAgain = Execute(runtime, actor, deck.Id, deck.Id, "k-scan-3", scan);
        Equal(ActionStatus.Committed, scanAgain.Status, "the scan runs once more");
        clock.Advance(runtime.Knowledge.StaleAfterMilliseconds + 700_000);
        runtime.Knowledge.Prune();
        Equal(KnowledgeLevel.Unknown, runtime.Knowledge.Level(actor, deck.Id),
            "a record older than the forget window is gone");

        // The store is bounded per observer: the oldest entries make room for new ones.
        var bounded = new KnowledgeStore(clock, new KnowledgeOptions(MaxPerObserver: 2));
        var observer = runtime.NewPrincipal();
        for (ulong i = 1; i <= 4; i++)
            bounded.Remember(observer, new HostId(i), KnowledgeLevel.Observed, 100);
        Equal(2, bounded.Known(observer, 10).Length, "the store keeps no more than its limit");
        Equal(KnowledgeLevel.Unknown, bounded.Level(observer, new HostId(1)), "the oldest record was evicted");
        Equal(KnowledgeLevel.Observed, bounded.Level(observer, new HostId(4)), "the newest record was kept");

        // A quarantined host is never discovered, so it never enters knowledge.
        runtime.Authority.Grant(actor, hidden.Id, CapabilityCategory.Discovery, null, 120_000, GrantSource.Granted, PrincipalId.None);
        var quarantine = runtime.RegisterHost(new HostDefinition("quar.1", HostClass.Server,
            Compute: new ComputeComponent(2, 1), Security: new SecurityComponent(SecurityProfile.Quarantine, 1, 1))).Host!;
        runtime.Authority.Grant(actor, quarantine.Id, CapabilityCategory.Discovery, null, 120_000, GrantSource.Granted, PrincipalId.None);
        Execute(runtime, actor, deck.Id, deck.Id, "k-scan-4", scan);
        Equal(KnowledgeLevel.Unknown, runtime.Knowledge.Level(actor, quarantine.Id),
            "a quarantined host never enters knowledge");
    }

    private static ActionOutcome Execute(NetRunRuntime runtime, PrincipalId actor, HostId execution, HostId target,
        string key, IGameAction action) =>
        runtime.Actions.Execute(Request(actor, execution, target, key), action);

    private static ActionRequest Request(PrincipalId actor, HostId execution, HostId target, string key) =>
        new(actor, execution, target, EntityId.None, ProcessId.None, CorrelationId.None, key);
}
