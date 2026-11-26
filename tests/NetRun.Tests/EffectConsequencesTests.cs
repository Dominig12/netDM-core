using NetRun.Core;
using static NetRun.Tests.Harness;

namespace NetRun.Tests;

/// <summary>
/// PRIM-003: an effect is not a log entry. A drain takes compute, damage reaches the core that lives on the
/// target, a shield resists the next damage, and an effect that changes nothing says so.
/// </summary>
internal static class EffectConsequencesTests
{
    public static void Run()
    {
        Section("effect consequences");
        var clock = new ManualSimulationClock();
        var runtime = new NetRunRuntime(clock);
        var deck = runtime.RegisterHost(new HostDefinition("deck.1", HostClass.Cyberdeck,
            Compute: new ComputeComponent(40, 10), Memory: new MemoryComponent(4))).Host!;
        var server = runtime.RegisterHost(new HostDefinition("server.1", HostClass.Server,
            Compute: new ComputeComponent(20, 5), Memory: new MemoryComponent(4))).Host!;
        var actor = runtime.NewPrincipal();

        // Drain takes compute out of the target's buffer, no more than is there.
        var before = runtime.Resources.Snapshot(server.Id)!.BufferMilliCu;
        var drain = runtime.Effects.Apply(EffectKind.Drain, server.Id, actor, 3, 3_000, "test");
        Equal(EffectStatus.Applied, drain.Status, "the drain is applied");
        Equal(3, drain.Applied, "the drain reports what it actually took");
        Equal(before - 3_000L, runtime.Resources.Snapshot(server.Id)!.BufferMilliCu,
            "the target's buffer really lost the compute");
        // A stacked drain repeats its stored magnitude, and never takes more than the host still has.
        var stacked = runtime.Effects.Apply(EffectKind.Drain, server.Id, actor, 3, 3_000, "test-2");
        Equal(3, stacked.Applied, "a stacked drain repeats the magnitude it holds");
        var tiny = runtime.RegisterHost(new HostDefinition("tiny.1", HostClass.Terminal,
            Compute: new ComputeComponent(2, 0), Memory: new MemoryComponent(1))).Host!;
        var greedy = runtime.Effects.Apply(EffectKind.Drain, tiny.Id, actor, 5, 3_000, "test-3");
        Equal(2, greedy.Applied, "a drain cannot take more compute than the host has");
        Equal(0L, runtime.Resources.Snapshot(tiny.Id)!.BufferMilliCu, "the buffer is empty, not negative");

        // Damage reaches the core living on the host.
        var identity = runtime.NewIdentity();
        runtime.Lives.Create(identity, actor, deck.Id);
        Equal(100, runtime.Lives.Get(identity)!.Integrity, "a new core starts intact");
        var damage = runtime.Effects.Apply(EffectKind.Damage, deck.Id, actor, 25, 0, "test");
        Equal(25, damage.Applied, "the damage is applied to the core on that host");
        Equal(75, runtime.Lives.Get(identity)!.Integrity, "the core lost exactly the damage");

        // A shield resists the next damage instead of the damage ignoring it.
        var shield = runtime.Effects.Apply(EffectKind.Shield, deck.Id, actor, 10, 10_000, "test");
        Check(shield.Status is EffectStatus.Applied or EffectStatus.Refreshed, "the shield is live");
        var shielded = runtime.Effects.Apply(EffectKind.Damage, deck.Id, actor, 25, 0, "test");
        Equal(15, shielded.Applied, "the shield reduced the incoming damage");
        Equal(60, runtime.Lives.Get(identity)!.Integrity, "the core lost only the unresisted part");

        // Repair restores the core, never above full.
        var repair = runtime.Effects.Apply(EffectKind.Repair, deck.Id, actor, 30, 0, "test");
        Equal(30, repair.Applied, "repair restores what the definition allows");
        Equal(90, runtime.Lives.Get(identity)!.Integrity, "the core recovered by that amount");
        var topped = runtime.Effects.Apply(EffectKind.Repair, deck.Id, actor, 30, 0, "test");
        Equal(10, topped.Applied, "repair restores only what was missing");
        Equal(100, runtime.Lives.Get(identity)!.Integrity, "the core is intact again");

        // An effect on a host without the state it names changes nothing, and says so.
        var empty = runtime.RegisterHost(new HostDefinition("relay.1", HostClass.Relay)).Host!;
        var idle = runtime.Effects.Apply(EffectKind.Damage, empty.Id, actor, 10, 0, "test");
        Equal(0, idle.Applied, "damage to a host with no core changes nothing");
        var drainEmpty = runtime.Effects.Apply(EffectKind.Drain, empty.Id, actor, 5, 1_000, "test");
        Equal(0, drainEmpty.Applied, "a drain on a host without compute takes nothing");

        // Perception effects stay live for their readers and report their magnitude.
        var cloak = runtime.Effects.Apply(EffectKind.Cloak, server.Id, actor, 1, 5_000, "test");
        Equal(1, cloak.Applied, "a perception effect is live and visible to readers");
        Check(runtime.Effects.On(server.Id, 8).Any(effect => effect.Kind == EffectKind.Cloak),
            "the cloak is on the target");
    }
}
