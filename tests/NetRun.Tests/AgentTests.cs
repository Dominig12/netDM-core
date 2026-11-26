using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// §12 / AGENT-001: viruses and ICE are processes that live on hosts, cost resources, spread only over existing
/// links, are bounded by quota and die with their host.
/// </summary>
internal static class AgentTests
{
    private sealed class Graph : IAgentNetwork
    {
        public Dictionary<ulong, ulong[]> Edges = [];

        public HostId[] Neighbours(HostId host) =>
            Edges.TryGetValue(host.Value, out var edges) ? edges.Select(node => new HostId(node)).ToArray() : [];

        public void Link(HostId a, HostId b)
        {
            Edges[a.Value] = Edges.TryGetValue(a.Value, out var first) ? [.. first, b.Value] : [b.Value];
            Edges[b.Value] = Edges.TryGetValue(b.Value, out var second) ? [.. second, a.Value] : [a.Value];
        }

        public void Cut(HostId a, HostId b)
        {
            if (Edges.TryGetValue(a.Value, out var fromA))
                Edges[a.Value] = fromA.Where(node => node != b.Value).ToArray();
            if (Edges.TryGetValue(b.Value, out var fromB))
                Edges[b.Value] = fromB.Where(node => node != a.Value).ToArray();
        }
    }

    private static readonly AgentDefinition Virus = new("test.virus", AgentKind.Virus,
        ComputeCu: 2, MemorySlots: 1, SpreadIntervalMilliseconds: 1_000, MaxGeneration: 3,
        MaxCopiesPerHost: 2, IntegrityLossPerHop: 5);

    private static readonly AgentDefinition Ice = new("test.ice", AgentKind.Ice,
        ComputeCu: 1, MemorySlots: 1, SpreadIntervalMilliseconds: 5_000, MaxGeneration: 0,
        MaxCopiesPerHost: 1, IntegrityLossPerHop: 0);

    /// <summary>The JSON payload of an action outcome, as the client would read it.</summary>
    private static string Text(ActionOutcome outcome) => outcome.Result ?? string.Empty;

    public static void Run()
    {
        Section("agents");
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var graph = new Graph();
        runtime.Agents.AttachNetwork(graph);
        var a = runtime.RegisterHost(new HostDefinition("a.1", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4))).Host!;
        var b = runtime.RegisterHost(new HostDefinition("b.1", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4))).Host!;
        var isolated = runtime.RegisterHost(new HostDefinition("c.1", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4))).Host!;
        graph.Link(a.Id, b.Id);
        var actor = runtime.NewPrincipal();

        // Deploying costs the host its compute: an agent is not a free tag.
        var bufferBefore = runtime.Resources.Snapshot(a.Id)!.BufferMilliCu;
        var deployed = runtime.Agents.Deploy(actor, a.Id, Virus, budget: 3, integrity: 100);
        Equal(AgentStatus.Ok, deployed.Status, "a virus is placed on a host that can pay for it");
        Check(runtime.Resources.Snapshot(a.Id)!.BufferMilliCu < bufferBefore, "the strain costs the host compute");
        Equal(a.Id, deployed.Agent!.Host, "it lives where it was placed");

        // It does not spread before its interval, and then only to an adjacent host.
        runtime.Tick();
        Equal(0, runtime.Agents.On(b.Id, 8).Length, "nothing spreads before the interval");
        clock.Advance(1_001);
        runtime.Tick();
        Equal(1, runtime.Agents.On(b.Id, 8).Length, "the virus copied itself to the neighbour");
        Equal(0, runtime.Agents.On(isolated.Id, 8).Length, "a host with no link is not reached");

        // A copy is weaker than its parent and carries the next generation.
        var child = runtime.Agents.On(b.Id, 1)[0];
        Check(child.Generation == 2, "the copy is the next generation");
        Equal(95, child.Integrity, "the copy lost integrity on the way");
        Check(child.SpreadsLeft < deployed.Agent.SpreadsLeft, "the copy inherits a smaller budget");

        // Spreading stops at the generation cap even with budget left.
        for (var step = 0; step < 6; step++)
        {
            clock.Advance(1_001);
            runtime.Tick();
        }
        Check(runtime.Agents.On(a.Id, 8).All(agent => agent.Generation <= Virus.MaxGeneration),
            "no copy outlives the strain's generation cap");

        // The host limit is enforced: a second strain of ICE cannot double up on one host.
        Equal(AgentStatus.Ok, runtime.Agents.Deploy(actor, isolated.Id, Ice, 0, 100).Status, "ICE is placed");
        Equal(AgentStatus.LimitExceeded, runtime.Agents.Deploy(actor, isolated.Id, Ice, 0, 100).Status,
            "ICE does not stack on one host");
        runtime.Tick();
        Equal(0, runtime.Agents.On(isolated.Id, 8).Count(agent => agent.Kind == AgentKind.Virus), "ICE never spreads");

        // A host that cannot pay is not infected for free.
        var poor = runtime.RegisterHost(new HostDefinition("poor.1", HostClass.Terminal,
            Compute: new ComputeComponent(1, 0), Memory: new MemoryComponent(1))).Host!;
        graph.Link(a.Id, poor.Id);
        runtime.Resources.Drain(poor.Id, 1_000);
        var refused = runtime.Agents.Deploy(actor, poor.Id, Virus, 1, 100);
        Equal(AgentStatus.InsufficientResources, refused.Status, "a strain is refused where it cannot run");

        // Cleaning an agent belongs to its owner; a purge clears a host regardless of ownership.
        var owned = runtime.Agents.On(b.Id, 1).FirstOrDefault();
        if (owned is not null)
        {
            Equal(AgentStatus.Forbidden, runtime.Agents.Clean(runtime.NewPrincipal(), owned.Id).Status,
                "another owner cannot clean your agent");
            Equal(AgentStatus.Ok, runtime.Agents.Clean(actor, owned.Id).Status, "its owner can");
        }
        var onA = runtime.Agents.On(a.Id, 8).Length;
        Check(onA > 0 && runtime.Agents.Purge(a.Id) == onA, "a purge clears every agent on the host");
        Equal(0, runtime.Agents.On(a.Id, 8).Length, "the host is clean");

        // Agents die with their host.
        var doomed = runtime.RegisterHost(new HostDefinition("doomed.1", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4))).Host!;
        runtime.Agents.Deploy(actor, doomed.Id, Ice, 0, 100);
        Check(runtime.Agents.On(doomed.Id, 8).Length == 1, "the ICE is there");
        runtime.DestroyHost(doomed.Id, doomed.Generation);
        Equal(0, runtime.Agents.On(doomed.Id, 8).Length, "the agent died with its host");

        // ICE defends the host it sits on: the same breach that wins against the bare profile now loses, and
        // the attempt is always noticed (§12.2).
        var hardened = runtime.RegisterHost(new HostDefinition("hardened.1", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4),
            Security: new SecurityComponent(SecurityProfile.Standard, 4, 2))).Host!;
        var breacher = runtime.NewPrincipal();
        runtime.Authority.Grant(breacher, hardened.Id, CapabilityCategory.Execution, null, 600_000,
            GrantSource.Granted, PrincipalId.None);
        runtime.Authority.Grant(breacher, hardened.Id, CapabilityCategory.Read, null, 600_000,
            GrantSource.Granted, PrincipalId.None);
        runtime.Authority.Grant(breacher, hardened.Id, CapabilityCategory.Discovery, null, 600_000,
            GrantSource.Granted, PrincipalId.None);
        runtime.Knowledge.Remember(breacher, hardened.Id, KnowledgeLevel.Observed, 90);
        var stealthBreach = runtime.Actions.GetAction("security.breach.stealth")!;
        var bare = runtime.Actions.Execute(new ActionRequest(breacher, hardened.Id, hardened.Id, EntityId.None,
            ProcessId.None, CorrelationId.None, "br-bare"), stealthBreach);
        Equal(ActionStatus.Committed, bare.Status, "a normal breach runs against a standard host");
        Check(Text(bare).Contains("\"success\":true", StringComparison.Ordinal),
            "without ICE a quiet attempt takes a standard host");
        runtime.Agents.Deploy(actor, hardened.Id, Ice, 0, 100);
        // The breach has its own cooldown: wait it out like a player would.
        clock.Advance(4_100);
        var guarded = runtime.Actions.Execute(new ActionRequest(breacher, hardened.Id, hardened.Id, EntityId.None,
            ProcessId.None, CorrelationId.None, "br-ice"), stealthBreach);
        Equal(ActionStatus.Committed, guarded.Status, "the attempt still runs, it just has a harder time");
        var guardedText = Text(guarded);
        Check(guardedText.Contains("\"success\":false", StringComparison.Ordinal),
            "ICE raises the bar enough to stop the same quiet attempt");
        Check(guardedText.Contains("\"detected\":true", StringComparison.Ordinal),
            "a defended host always notices the attempt");

        // The owner sees its own agents and nobody else''s.
        var stranger = runtime.NewPrincipal();
        var fresh = runtime.RegisterHost(new HostDefinition("fresh.1", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4))).Host!;
        var strangerAgent = runtime.Agents.Deploy(stranger, fresh.Id, Ice, 0, 100);
        Equal(AgentStatus.Ok, strangerAgent.Status, "a second owner places its own");
        Check(runtime.Agents.OwnedBy(stranger, 8).All(agent => agent.Owner == stranger),
            "an owner sees only its own agents");
        Check(runtime.Agents.OwnedBy(stranger, 8).Length >= 1 && runtime.Agents.OwnedBy(actor, 8).All(agent => agent.Owner == actor),
            "the listings are separate");
    }
}
