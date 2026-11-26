using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// §8.2 abilities. DISRUPT, DRAIN, SHIELD, CLOAK and REVEAL are ordinary registered actions over registered
/// effects: the action owns the numbers, the registry enforces its definition, and the target's own state takes
/// the consequence. A player, a preset and a program name the same action and meet the same rules.
/// </summary>
internal static class AbilityTests
{
    public static void Run()
    {
        Section("abilities: disrupt, drain, shield, cloak");
        DisruptAndDrain();
        ShieldResistsTheHit();
        CloakAndReveal();
        Boundaries();
    }

    /// <summary>An ability reaches the core that lives on the target, and the host notices the hit.</summary>
    private static void DisruptAndDrain()
    {
        var (clock, runtime, deck, target) = World();
        _ = clock;
        var actor = runtime.NewPrincipal();
        var victim = runtime.NewPrincipal();
        var life = runtime.NewIdentity();
        Check(runtime.Lives.Create(life, victim, target.Id).Succeeded, "the victim has a core on the target");
        Allow(runtime, actor, deck.Id, CapabilityCategory.Execution);
        Allow(runtime, actor, deck.Id, CapabilityCategory.Discovery);
        Allow(runtime, actor, target.Id, CapabilityCategory.Control);
        runtime.Knowledge.Remember(actor, target.Id, KnowledgeLevel.Observed, 90);

        var disrupt = runtime.Actions.GetAction("net.disrupt")!;
        var hit = runtime.Actions.Execute(new ActionRequest(actor, deck.Id, target.Id, EntityId.None,
            ProcessId.None, CorrelationId.None, "disrupt-1"), disrupt);
        Equal(ActionStatus.Committed, hit.Status, $"the disrupt lands: {hit.Reason}");
        Equal(90, runtime.Lives.Get(life)!.Integrity, "the core on the target lost real integrity");
        Check(runtime.Alerts.Level(target.Id) >= HostAlertLevel.Suspicious,
            $"the target noticed the hit ({runtime.Alerts.Level(target.Id)})");

        var bufferBefore = runtime.Resources.Snapshot(target.Id)!.BufferMilliCu;
        var drain = runtime.Actions.GetAction("net.drain")!;
        var drained = runtime.Actions.Execute(new ActionRequest(actor, deck.Id, target.Id, EntityId.None,
            ProcessId.None, CorrelationId.None, "drain-1"), drain);
        Equal(ActionStatus.Committed, drained.Status, $"the drain lands: {drained.Reason}");
        Equal(bufferBefore - 6_000, runtime.Resources.Snapshot(target.Id)!.BufferMilliCu,
            "the drain took the promised compute out of the target's buffer");
    }

    /// <summary>SHIELD is real resistance: the same hit costs the target one point instead of a full one.</summary>
    private static void ShieldResistsTheHit()
    {
        var (clock, runtime, deck, target) = World();
        _ = clock;
        var actor = runtime.NewPrincipal();
        var victim = runtime.NewPrincipal();
        var life = runtime.NewIdentity();
        Check(runtime.Lives.Create(life, victim, target.Id).Succeeded, "the victim has a core on the target");
        Allow(runtime, actor, deck.Id, CapabilityCategory.Execution);
        Allow(runtime, actor, deck.Id, CapabilityCategory.Discovery);
        Allow(runtime, actor, target.Id, CapabilityCategory.Control);
        runtime.Knowledge.Remember(actor, target.Id, KnowledgeLevel.Observed, 90);

        var shield = runtime.Actions.GetAction("net.shield")!;
        var raised = runtime.Actions.Execute(new ActionRequest(actor, deck.Id, target.Id, EntityId.None,
            ProcessId.None, CorrelationId.None, "shield-1"), shield);
        Equal(ActionStatus.Committed, raised.Status, $"the shield goes up: {raised.Reason}");
        Equal(HostAlertLevel.Normal, runtime.Alerts.Level(target.Id),
            "putting up a defence does not raise the defender's own alarm");

        var disrupt = runtime.Actions.GetAction("net.disrupt")!;
        var hit = runtime.Actions.Execute(new ActionRequest(actor, deck.Id, target.Id, EntityId.None,
            ProcessId.None, CorrelationId.None, "disrupt-2"), disrupt);
        Equal(ActionStatus.Committed, hit.Status, $"the hit lands: {hit.Reason}");
        Equal(99, runtime.Lives.Get(life)!.Integrity,
            "the shield absorbed all but the minimum one point of damage");
    }

    /// <summary>CLOAK hides what stands on a host; REVEAL unmasks it; both end on their own.</summary>
    private static void CloakAndReveal()
    {
        var (clock, runtime, deck, target) = World();
        var actor = runtime.NewPrincipal();
        Allow(runtime, actor, deck.Id, CapabilityCategory.Execution);
        Allow(runtime, actor, deck.Id, CapabilityCategory.Control);
        Allow(runtime, actor, target.Id, CapabilityCategory.Control);
        runtime.Knowledge.Remember(actor, target.Id, KnowledgeLevel.Observed, 90);

        Check(!runtime.Effects.IsMasked(deck.Id), "a fresh host is not masked");
        var cloak = runtime.Actions.GetAction("net.cloak")!;
        var hidden = runtime.Actions.Execute(new ActionRequest(actor, deck.Id, deck.Id, EntityId.None,
            ProcessId.None, CorrelationId.None, "cloak-1"), cloak);
        Equal(ActionStatus.Committed, hidden.Status, $"the cloak goes up: {hidden.Reason}");
        Check(runtime.Effects.IsMasked(deck.Id), "the host is masked while the cloak lives");

        // A cloak paid for by its owner is not an intrusion; the mask itself is the whole point.
        Equal(HostAlertLevel.Normal, runtime.Alerts.Level(deck.Id), "cloaking your own host raises no alarm");

        var reveal = runtime.Actions.GetAction("net.reveal")!;
        var shown = runtime.Actions.Execute(new ActionRequest(actor, deck.Id, deck.Id, EntityId.None,
            ProcessId.None, CorrelationId.None, "reveal-1"), reveal);
        Equal(ActionStatus.Committed, shown.Status, $"the reveal lands: {shown.Reason}");
        Check(!runtime.Effects.IsMasked(deck.Id), "a revealed host is not masked any more");

        // The mask expires on its own even if nothing reveals it: an effect without a reader still ages.
        clock.Advance(11_000);
        runtime.Tick();
        var again = runtime.Actions.Execute(new ActionRequest(actor, deck.Id, deck.Id, EntityId.None,
            ProcessId.None, CorrelationId.None, "cloak-2"), cloak);
        Equal(ActionStatus.Committed, again.Status, $"the second cloak goes up after its cooldown: {again.Reason}");
        Check(runtime.Effects.IsMasked(deck.Id), "the second cloak masks again");
        clock.Advance(16_000);
        runtime.Tick();
        Check(!runtime.Effects.IsMasked(deck.Id), "the mask wore off with its duration");
    }

    /// <summary>What an ability may not do: reach an unknown host, work without control, or ignore its cooldown.</summary>
    private static void Boundaries()
    {
        var (clock, runtime, deck, target) = World();
        var stranger = runtime.RegisterHost(new HostDefinition("stranger.ability", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4))).Host!;
        var actor = runtime.NewPrincipal();
        Allow(runtime, actor, deck.Id, CapabilityCategory.Execution);
        Allow(runtime, actor, deck.Id, CapabilityCategory.Discovery);
        var disrupt = runtime.Actions.GetAction("net.disrupt")!;

        // No control over the target: one refusal, and it says nothing about whether the target exists.
        var refused = runtime.Actions.Execute(new ActionRequest(actor, deck.Id, target.Id, EntityId.None,
            ProcessId.None, CorrelationId.None, "ability-denied"), disrupt);
        Equal(ActionStatus.Forbidden, refused.Status, "control over the target is required");

        Allow(runtime, actor, target.Id, CapabilityCategory.Control);
        runtime.Knowledge.Remember(actor, target.Id, KnowledgeLevel.Observed, 90);
        var known = runtime.Actions.Execute(new ActionRequest(actor, deck.Id, target.Id, EntityId.None,
            ProcessId.None, CorrelationId.None, "ability-known"), disrupt);
        Equal(ActionStatus.Committed, known.Status, $"the ability works on a controlled, known host: {known.Reason}");

        // The cooldown belongs to the principal and the ability, not to the file or the target.
        var twice = runtime.Actions.Execute(new ActionRequest(actor, deck.Id, target.Id, EntityId.None,
            ProcessId.None, CorrelationId.None, "ability-cooldown"), disrupt);
        Equal(ActionStatus.Cooldown, twice.Status, "the second hit waits for the cooldown");

        clock.Advance(4_000);
        runtime.Tick();
        Allow(runtime, actor, stranger.Id, CapabilityCategory.Control);
        var unknown = runtime.Actions.Execute(new ActionRequest(actor, deck.Id, stranger.Id, EntityId.None,
            ProcessId.None, CorrelationId.None, "ability-unknown"), disrupt);
        Equal(ActionStatus.InvalidState, unknown.Status, "a host the actor never learned is not a target");
        Equal("not_known", unknown.Reason, "and the reason names knowledge, not existence");
    }

    private static (ManualSimulationClock Clock, NetRunRuntime Runtime, HostSnapshot Deck, HostSnapshot Target) World()
    {
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.ability", HostClass.Cyberdeck,
            Compute: new ComputeComponent(60, 20), Memory: new MemoryComponent(4))).Host!;
        var target = runtime.RegisterHost(new HostDefinition("target.ability", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4),
            Security: new SecurityComponent(SecurityProfile.Standard, 4, 2))).Host!;
        return (clock, runtime, deck, target);
    }

    private static void Allow(NetRunRuntime runtime, PrincipalId principal, HostId host, CapabilityCategory category) =>
        runtime.Authority.Grant(principal, host, category, null, 600_000, GrantSource.Granted, PrincipalId.None);
}
