using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// §9.3 end to end: a breach the host notices raises its own alert, and a host at Lockdown stops
/// answering ordinary authority while staying breakable.
/// </summary>
internal static class AlertIntegrationTests
{
    public static void Run()
    {
        Section("alerts: breach raises, lockdown denies");
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.1", HostClass.Cyberdeck,
            Compute: new ComputeComponent(60, 20), Memory: new MemoryComponent(4))).Host!;
        var target = runtime.RegisterHost(new HostDefinition("target.1", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4),
            Security: new SecurityComponent(SecurityProfile.Standard, 4, 2))).Host!;
        var actor = runtime.NewPrincipal();
        foreach (var grant in new[]
                 {
                     (Host: deck.Id, Category: CapabilityCategory.Execution),
                     (Host: deck.Id, Category: CapabilityCategory.Discovery),
                     (Host: target.Id, Category: CapabilityCategory.Discovery)
                 })
        {
            runtime.Authority.Grant(actor, grant.Host, grant.Category, null, 600_000,
                GrantSource.Granted, PrincipalId.None);
        }
        runtime.Knowledge.Remember(actor, target.Id, KnowledgeLevel.Observed, 90);

        Equal(HostAlertLevel.Normal, runtime.Alerts.Level(target.Id), "an untouched host is normal");
        var action = runtime.Actions.GetAction("security.breach.forced")!;
        var outcome = runtime.Actions.Execute(new ActionRequest(actor, deck.Id, target.Id,
            EntityId.None, ProcessId.None, CorrelationId.None, "alert-breach"), action);
        Equal(ActionStatus.Committed, outcome.Status, $"a forced breach runs: {outcome.Reason}");
        Check(runtime.Alerts.Level(target.Id) >= HostAlertLevel.Alert,
            $"a forced attempt that was noticed raises the host's alert ({runtime.Alerts.Level(target.Id)})");
        Check((outcome.Result ?? "").Contains("alert", StringComparison.Ordinal),
            "the verdict names the alert the attempt met");

        // Ordinary authority keeps working while the host is only alarmed; at Lockdown it stops. The temporary
        // control the attempt won is dropped first: it survives a lockdown by design (the lockdown must stay
        // breakable), and leaving it in place would hide the rule this check is about.
        runtime.Authority.RevokeAll(actor);
        runtime.Authority.Grant(actor, target.Id, CapabilityCategory.Control, null, 600_000,
            GrantSource.Granted, PrincipalId.None);
        Equal(AuthorizationStatus.Ok, runtime.Authority.Authorize(actor, target.Id,
            CapabilityCategory.Control, null, SecurityProfile.Standard).Status,
            "a granted control works while the host is not locked down");
        runtime.Alerts.Raise(target.Id, 10, "test");
        Equal(HostAlertLevel.Lockdown, runtime.Alerts.Level(target.Id), "the alert reaches Lockdown");
        Equal(AuthorizationStatus.PolicyDenied, runtime.Authority.Authorize(actor, target.Id,
            CapabilityCategory.Control, null, SecurityProfile.Standard, lockdown: true).Status,
            "lockdown refuses ordinary authority even with a grant");
        Equal(AuthorizationStatus.Ok, runtime.Authority.Authorize(actor, target.Id,
            CapabilityCategory.Control, null, SecurityProfile.Standard).Status,
            "the grant itself is untouched: only the pipeline's lockdown flag refuses it");

        // A lockdown is not absolute: what a breach won still opens the host (SEC-002, §9.2).
        runtime.Authority.Grant(actor, target.Id, CapabilityCategory.Control, null, 10_000,
            GrantSource.Breach, PrincipalId.None);
        Equal(AuthorizationStatus.Ok, runtime.Authority.Authorize(actor, target.Id,
            CapabilityCategory.Control, null, SecurityProfile.Standard, lockdown: true).Status,
            "a breach grant still opens a host that is locked down");

        AlertTravelsOneHop();
    }

    /// <summary>
    /// §9.3: an alarm reaches the hosts adjacent on the published graph — one hop, over links that exist,
    /// and no further. The reporting host raised its own level in the breach; nothing lowers anything.
    /// </summary>
    private static void AlertTravelsOneHop()
    {
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.1", HostClass.Cyberdeck,
            Compute: new ComputeComponent(60, 20), Memory: new MemoryComponent(4))).Host!;
        var victim = runtime.RegisterHost(new HostDefinition("victim.1", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4),
            Security: new SecurityComponent(SecurityProfile.Standard, 4, 2))).Host!;
        var neighbour = runtime.RegisterHost(new HostDefinition("neighbour.1", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4))).Host!;
        var stranger = runtime.RegisterHost(new HostDefinition("stranger.1", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4))).Host!;
        var graph = new LinkGraph();
        graph.Link(victim.Id, neighbour.Id);
        runtime.Agents.AttachNetwork(graph);

        var actor = runtime.NewPrincipal();
        foreach (var grant in new[]
                 {
                     (Host: deck.Id, Category: CapabilityCategory.Execution),
                     (Host: deck.Id, Category: CapabilityCategory.Discovery),
                     (Host: victim.Id, Category: CapabilityCategory.Discovery)
                 })
        {
            runtime.Authority.Grant(actor, grant.Host, grant.Category, null, 600_000,
                GrantSource.Granted, PrincipalId.None);
        }
        runtime.Knowledge.Remember(actor, victim.Id, KnowledgeLevel.Observed, 90);
        var action = runtime.Actions.GetAction("security.breach.forced")!;
        runtime.Actions.Execute(new ActionRequest(actor, deck.Id, victim.Id,
            EntityId.None, ProcessId.None, CorrelationId.None, "alert-hop"), action);
        runtime.PublishEvents();

        Check(runtime.Alerts.Level(neighbour.Id) >= HostAlertLevel.Suspicious,
            $"the adjacent host becomes suspicious ({runtime.Alerts.Level(neighbour.Id)})");
        Equal(HostAlertLevel.Normal, runtime.Alerts.Level(stranger.Id),
            "a host beyond one hop is not told: the alarm follows the existing links");
    }

    /// <summary>The published graph as the tests need it: undirected pairs of hosts.</summary>
    private sealed class LinkGraph : IAgentNetwork
    {
        private readonly Dictionary<ulong, ulong[]> _edges = [];

        public HostId[] Neighbours(HostId host) =>
            _edges.TryGetValue(host.Value, out var edges) ? edges.Select(node => new HostId(node)).ToArray() : [];

        public void Link(HostId a, HostId b)
        {
            _edges[a.Value] = _edges.TryGetValue(a.Value, out var first) ? [.. first, b.Value] : [b.Value];
            _edges[b.Value] = _edges.TryGetValue(b.Value, out var second) ? [.. second, a.Value] : [a.Value];
        }
    }
}
